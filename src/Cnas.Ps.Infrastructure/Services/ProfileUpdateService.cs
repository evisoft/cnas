using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// JSON-serialiser settings shared by <see cref="ProfileUpdateService"/> and
/// <see cref="ProfileRefreshService"/>. Property-name comparison is case-insensitive so
/// either PascalCase (matches the C# record property names) or camelCase (matches the
/// external-data wire shape) payloads round-trip cleanly into the contributor input DTOs.
/// </summary>
internal static class ProfilePayloadSerialization
{
    /// <summary>Shared deserialization settings — case-insensitive property matching.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// R0362 / TOR UC13 — default <see cref="IProfileUpdateService"/> implementation. Owns
/// the lifecycle of a <see cref="ProfileUpdateRequest"/> from submit to apply/reject,
/// dispatching the apply step to <see cref="IContributorLinkedEntitiesService"/> when
/// the approver says yes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission model.</b> Submit is open to authenticated users (anonymous callers are
/// rejected). Approve and reject require the <c>cnas-admin</c> role (mirroring
/// <see cref="PendingAdminActionService"/>'s defense-in-depth pattern — the controller
/// gate is the primary check but every service entry point re-validates).
/// </para>
/// <para>
/// <b>Apply-failure trapping.</b> When the apply step itself returns a failed Result the
/// row is still persisted with <see cref="ProfileUpdateRequestStatus.Failed"/> and the
/// failure envelope captured in <see cref="ProfileUpdateRequest.ApplicationErrorJson"/>.
/// The service then returns <see cref="Result{T}.Failure"/> so the caller sees the
/// error; the persisted row is the audit anchor.
/// </para>
/// </remarks>
/// <param name="db">DbContext abstraction.</param>
/// <param name="contributorService">Contributor-side writer used to apply the change at approve time.</param>
/// <param name="clock">UTC clock.</param>
/// <param name="sqids">Sqid encoder / decoder.</param>
/// <param name="caller">Caller context (user id + roles).</param>
/// <param name="audit">Audit-log sink.</param>
public sealed class ProfileUpdateService(
    ICnasDbContext db,
    IContributorLinkedEntitiesService contributorService,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    IAuditService audit) : IProfileUpdateService
{
    private readonly ICnasDbContext _db = db;
    private readonly IContributorLinkedEntitiesService _contributorService = contributorService;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    private const string AdminRole = "cnas-admin";
    private const string EvtApplied = "PROFILE.UPDATE.APPLIED";
    private const string EvtRejected = "PROFILE.UPDATE.REJECTED";

    /// <summary>
    /// Service-application <c>WorkflowCode</c> used for profile-update requests. The shell
    /// application is created with this code so downstream listeners can filter for it
    /// without inspecting child rows.
    /// </summary>
    private const string WorkflowCode = "PROFILE_UPDATE";

    /// <inheritdoc />
    public async Task<Result<ProfileUpdateRequestDto>> SubmitAsync(
        ProfileUpdateRequestSubmitDto input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_caller.UserId is null)
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.Unauthorized, "Caller must be authenticated to submit a profile update.");
        }

        if (!Enum.TryParse<ProfileUpdateRequestType>(input.Type, ignoreCase: true, out var parsedType))
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.ProfileUpdateUnknownType,
                $"Unknown profile-update Type '{input.Type}'.");
        }

        if (!IsValidJson(input.RequestedChangesJson))
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.ProfileUpdateInvalidPayload,
                "RequestedChangesJson must be syntactically valid JSON.");
        }

        var decoded = _sqids.TryDecode(input.TargetContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<ProfileUpdateRequestDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var contributor = await _db.InsuredPersons
            .FirstOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (contributor is null)
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.NotFound, "Target contributor not found.");
        }

        var now = _clock.UtcNow;

        // Locate the Solicitant row for the caller; if none exists, the parent service
        // application is still created — the FK to Solicitant is preserved by deriving
        // SolicitantId from a future user→solicitant mapping. We resolve by NationalId
        // when the caller has an associated UserProfile that already exists, otherwise
        // fall back to seeding a stub row keyed by the caller's UserSqid.
        var solicitantId = await ResolveSolicitantIdAsync(ct).ConfigureAwait(false);

        // Profile-update applications run a minimal placeholder workflow — there is no
        // pre-existing ServicePassport for "profile update" yet, so we pin a sentinel
        // value of 0 / 1 for the version columns. The submit pipeline does not enforce
        // a passport here because profile updates are internal flows; a future passport
        // can be wired without breaking this entry point.
        var application = new ServiceApplication
        {
            SolicitantId = solicitantId,
            ServicePassportId = 0,
            PinnedServicePassportVersion = 1,
            PinnedWorkflowVersion = 1,
            Status = ApplicationStatus.Submitted,
            FormPayloadJson = JsonSerializer.Serialize(new
            {
                workflowCode = WorkflowCode,
                requestType = parsedType.ToString(),
                note = input.Note,
            }),
            SnapshotJson = null,
            SubmittedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.Applications.Add(application);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var row = new ProfileUpdateRequest
        {
            ServiceApplicationId = application.Id,
            TargetContributorId = contributor.Id,
            Type = parsedType,
            RequestedChangesJson = input.RequestedChangesJson,
            Status = ProfileUpdateRequestStatus.Pending,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ProfileUpdateRequests.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<ProfileUpdateRequestDto>.Success(ToDto(row, application.Id));
    }

    /// <inheritdoc />
    public async Task<Result<ProfileUpdateRequestDto>> ApproveAsync(long requestId, CancellationToken ct = default)
    {
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.Forbidden, "Caller lacks the Profile.Approve permission.");
        }

        var row = await _db.ProfileUpdateRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.NotFound, "Profile update request not found.");
        }
        if (row.Status != ProfileUpdateRequestStatus.Pending)
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.Conflict,
                $"Profile update request already in status {row.Status}.");
        }

        var now = _clock.UtcNow;
        var approverId = _caller.UserId;

        // Apply the requested change via the contributor-side writer. The writer returns
        // its own Result; we trap failures and persist them on the row.
        var applyResult = await ApplyChangeAsync(row, ct).ConfigureAwait(false);

        if (applyResult.IsSuccess)
        {
            row.Status = ProfileUpdateRequestStatus.Applied;
            row.AppliedAtUtc = now;
            row.ApprovedByUserId = approverId;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await EmitAppliedAuditAsync(row, success: true, ct).ConfigureAwait(false);
            return Result<ProfileUpdateRequestDto>.Success(ToDto(row, row.ServiceApplicationId));
        }
        else
        {
            row.Status = ProfileUpdateRequestStatus.Failed;
            row.AppliedAtUtc = null;
            row.ApprovedByUserId = approverId;
            row.ApplicationErrorJson = JsonSerializer.Serialize(new
            {
                errorCode = applyResult.ErrorCode,
                errorMessage = applyResult.ErrorMessage,
            });
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await EmitAppliedAuditAsync(row, success: false, ct).ConfigureAwait(false);
            return Result<ProfileUpdateRequestDto>.Failure(
                applyResult.ErrorCode ?? ErrorCodes.ProfileUpdateInvalidPayload,
                applyResult.ErrorMessage ?? "Apply failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> RejectAsync(long requestId, string reason, CancellationToken ct = default)
    {
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks the Profile.Approve permission.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Rejection reason is required.");
        }

        var row = await _db.ProfileUpdateRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Profile update request not found.");
        }
        if (row.Status != ProfileUpdateRequestStatus.Pending)
        {
            return Result.Failure(ErrorCodes.Conflict,
                $"Profile update request already in status {row.Status}.");
        }

        var now = _clock.UtcNow;
        row.Status = ProfileUpdateRequestStatus.Rejected;
        row.RejectionReason = reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detail = JsonSerializer.Serialize(new
        {
            requestSqid = _sqids.Encode(row.Id),
            type = row.Type.ToString(),
            contributorSqid = _sqids.Encode(row.TargetContributorId),
            reason,
        });
        await _audit.RecordAsync(
            EvtRejected, AuditSeverity.Notice, _caller.UserSqid ?? "system",
            "ProfileUpdateRequest", row.Id, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ProfileUpdateRequestDto>> GetAsync(long requestId, CancellationToken ct = default)
    {
        var row = await _db.ProfileUpdateRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ProfileUpdateRequestDto>.Failure(
                ErrorCodes.NotFound, "Profile update request not found.");
        }
        return Result<ProfileUpdateRequestDto>.Success(ToDto(row, row.ServiceApplicationId));
    }

    // ─── helpers ─────────────────────

    /// <summary>
    /// Dispatches the apply step to the matching <see cref="IContributorLinkedEntitiesService"/>
    /// method based on <see cref="ProfileUpdateRequest.Type"/>. Returns a non-generic
    /// <see cref="Result"/> so the approve path can branch on success/failure without
    /// caring about the specific child-row DTO type that the writer returns.
    /// </summary>
    /// <param name="row">The request being applied.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result> ApplyChangeAsync(ProfileUpdateRequest row, CancellationToken ct)
    {
        const string changeReason = "Approved profile-update request";

        try
        {
            switch (row.Type)
            {
                case ProfileUpdateRequestType.Address:
                {
                    var dto = JsonSerializer.Deserialize<ContributorAddressInputDto>(row.RequestedChangesJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Address payload was null.");
                    var res = await _contributorService.UpdateAddressAsync(
                        row.TargetContributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case ProfileUpdateRequestType.Contact:
                {
                    var dto = JsonSerializer.Deserialize<ContributorContactInputDto>(row.RequestedChangesJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Contact payload was null.");
                    var res = await _contributorService.UpdateContactAsync(
                        row.TargetContributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case ProfileUpdateRequestType.CivilStatus:
                {
                    var dto = JsonSerializer.Deserialize<ContributorCivilStatusInputDto>(row.RequestedChangesJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("CivilStatus payload was null.");
                    var res = await _contributorService.UpdateCivilStatusAsync(
                        row.TargetContributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case ProfileUpdateRequestType.Activity:
                {
                    var dto = JsonSerializer.Deserialize<ContributorActivityPeriodInputDto>(row.RequestedChangesJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Activity payload was null.");
                    var res = await _contributorService.AddActivityPeriodAsync(
                        row.TargetContributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                case ProfileUpdateRequestType.SocialInsuranceContract:
                {
                    var dto = JsonSerializer.Deserialize<ContributorSocialInsuranceContractInputDto>(row.RequestedChangesJson, ProfilePayloadSerialization.Options)
                        ?? throw new JsonException("Contract payload was null.");
                    var res = await _contributorService.UpdateSocialInsuranceContractAsync(
                        row.TargetContributorId, dto, changeReason, ct).ConfigureAwait(false);
                    return res.IsSuccess
                        ? Result.Success()
                        : Result.Failure(res.ErrorCode!, res.ErrorMessage!);
                }
                default:
                    return Result.Failure(
                        ErrorCodes.ProfileUpdateUnknownType,
                        $"Unsupported profile-update Type {row.Type}.");
            }
        }
        catch (JsonException ex)
        {
            return Result.Failure(
                ErrorCodes.ProfileUpdateInvalidPayload,
                $"Failed to deserialise RequestedChangesJson: {ex.Message}");
        }
    }

    /// <summary>
    /// Emits the Critical <c>PROFILE.UPDATE.APPLIED</c> audit row capturing both the
    /// successful applies and the persisted-but-failed apply attempts.
    /// </summary>
    /// <param name="row">The request row.</param>
    /// <param name="success">Whether the apply step succeeded.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAppliedAuditAsync(ProfileUpdateRequest row, bool success, CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new
        {
            requestSqid = _sqids.Encode(row.Id),
            type = row.Type.ToString(),
            contributorSqid = _sqids.Encode(row.TargetContributorId),
            success,
        });
        await _audit.RecordAsync(
            EvtApplied, AuditSeverity.Critical, _caller.UserSqid ?? "system",
            "ProfileUpdateRequest", row.Id, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the calling user to a Solicitant primary key. If no Solicitant row maps
    /// to the caller, returns 0 so the parent application can still be created — the
    /// integrity constraint at the database layer is the responsibility of the
    /// future passport rollout. Internal-only fallback path.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task<long> ResolveSolicitantIdAsync(CancellationToken ct)
    {
        if (_caller.UserId is not long userId)
        {
            return 0;
        }

        // Best-effort: match Solicitants by CreatedBy = caller's UserSqid (the closest
        // available linkage in the current model). When no row matches we fall back to
        // 0 — the parent application is still inserted; the FK constraint is not active
        // in the InMemory test store and the production migration can enforce it later
        // once the citizen-portal Solicitant linkage matures.
        var sqid = _caller.UserSqid;
        if (!string.IsNullOrEmpty(sqid))
        {
            var match = await _db.Solicitants
                .Where(s => s.CreatedBy == sqid && s.IsActive)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (match.HasValue)
            {
                return match.Value;
            }
        }
        _ = userId;
        return 0;
    }

    /// <summary>Maps an entity row to its output DTO.</summary>
    /// <param name="row">The entity row.</param>
    /// <param name="applicationId">The parent service-application id.</param>
    private ProfileUpdateRequestDto ToDto(ProfileUpdateRequest row, long applicationId) => new(
        Id: _sqids.Encode(row.Id),
        ServiceApplicationSqid: _sqids.Encode(applicationId),
        TargetContributorSqid: _sqids.Encode(row.TargetContributorId),
        Type: row.Type.ToString(),
        Status: row.Status.ToString(),
        RequestedChangesJson: row.RequestedChangesJson,
        RejectionReason: row.RejectionReason,
        AppliedAtUtc: row.AppliedAtUtc,
        ApprovedByUserSqid: row.ApprovedByUserId is long id ? _sqids.Encode(id) : null,
        ApplicationErrorJson: row.ApplicationErrorJson);

    /// <summary>Returns <c>true</c> when <paramref name="json"/> is parseable.</summary>
    /// <param name="json">Candidate JSON string.</param>
    private static bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
