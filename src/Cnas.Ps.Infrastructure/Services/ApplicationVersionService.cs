using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0321 / R0224 / UI 008 — default <see cref="IApplicationVersionService"/>
/// implementation. Owns the autosave / manual-save / submit / revert lifecycle for
/// <see cref="ApplicationVersion"/> rows and enforces the dedup + autosave-cap
/// invariants documented on the interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every public method runs a Sqid decode + ownership /
/// management-role check before touching the version table. The caller must either
/// be the application's solicitant or hold one of <c>cnas-decider</c> /
/// <c>cnas-admin</c> / <c>cnas-tech-admin</c>. Foreign callers receive
/// <see cref="ErrorCodes.Forbidden"/>; anonymous callers receive
/// <see cref="ErrorCodes.Unauthorized"/>.
/// </para>
/// <para>
/// <b>Editability gate.</b> A save is accepted when the parent
/// <see cref="ServiceApplication"/> is in <see cref="ApplicationStatus.Draft"/>; the
/// <see cref="ApplicationVersionSource.Submit"/> source is additionally allowed on
/// <see cref="ApplicationStatus.Submitted"/> so the submission ceremony can persist
/// its own final snapshot. Any other status surfaces as
/// <see cref="ErrorCodes.ApplicationNotEditable"/>.
/// </para>
/// <para>
/// <b>Dedup.</b> Before writing, the service ordinal-compares the supplied
/// <c>FormDataJson</c> against the current row's payload. A match short-circuits the
/// save: no new row, no version-number burn, the existing current row is returned
/// verbatim and the <see cref="CnasMeter.ApplicationVersionDedup"/> counter ticks.
/// </para>
/// <para>
/// <b>Autosave cap.</b> Only
/// <see cref="ApplicationVersionSource.Autosave"/> rows count toward the cap and only
/// they are eligible for pruning. When inserting the (N+1)-th autosave the service
/// hard-deletes the oldest autosave row in the same transaction; the counter
/// <see cref="CnasMeter.ApplicationVersionAutosavePruned"/> ticks per pruned row.
/// </para>
/// <para>
/// <b>Audit shape.</b> Successful saves emit an
/// <see cref="AuditSeverity.Information"/> row with code
/// <c>APPLICATION.VERSION.SAVED</c>; reverts emit an additional
/// <see cref="AuditSeverity.Notice"/> row with code <c>APPLICATION.VERSION.REVERTED</c>.
/// <c>DetailsJson</c> carries the application Sqid + version number(s) + source —
/// never the form payload, so a citizen's IDNP inadvertently captured in the form
/// cannot leak through the audit trail.
/// </para>
/// </remarks>
public sealed class ApplicationVersionService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    IAuditService audit,
    ICallerContext caller,
    IOptions<ApplicationAutosaveOptions> options)
    : IApplicationVersionService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly IAuditService _audit = audit;
    private readonly ICallerContext _caller = caller;
    private readonly ApplicationAutosaveOptions _opts = options.Value;

    /// <summary>Stable event-code for the per-save audit row (Information severity).</summary>
    private const string AuditPrefixSaved = "APPLICATION.VERSION.SAVED";

    /// <summary>Stable event-code for the per-revert audit row (Notice severity).</summary>
    private const string AuditPrefixReverted = "APPLICATION.VERSION.REVERTED";

    /// <summary>
    /// Role codes that grant cross-applicant Application.Manage authority. Holders
    /// satisfy the ownership gate even when they did not author the application.
    /// </summary>
    private static readonly string[] ManagementRoles = ["cnas-decider", "cnas-admin", "cnas-tech-admin"];

    /// <inheritdoc />
    public async Task<Result<ApplicationVersionOutputDto>> SaveAsync(
        string applicationSqid,
        string formDataJson,
        ApplicationVersionSource source,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formDataJson);

        var ownership = await ResolveOwnedApplicationAsync(applicationSqid, cancellationToken).ConfigureAwait(false);
        if (ownership.failure is { } failure)
        {
            return Result<ApplicationVersionOutputDto>.Failure(failure.code, failure.message);
        }

        return await SaveInternalAsync(ownership.application!, formDataJson, source, note, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationVersionOutputDto>> RevertAsync(
        string applicationSqid,
        int targetVersionNumber,
        CancellationToken cancellationToken = default)
    {
        var ownership = await ResolveOwnedApplicationAsync(applicationSqid, cancellationToken).ConfigureAwait(false);
        if (ownership.failure is { } failure)
        {
            return Result<ApplicationVersionOutputDto>.Failure(failure.code, failure.message);
        }
        var application = ownership.application!;

        var target = await _db.ApplicationVersions
            .SingleOrDefaultAsync(
                v => v.ServiceApplicationId == application.Id
                  && v.VersionNumber == targetVersionNumber,
                cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return Result<ApplicationVersionOutputDto>.Failure(
                ErrorCodes.NotFound,
                $"Application version {targetVersionNumber} not found.");
        }

        // Capture the target's "from" version BEFORE the SaveInternalAsync call (which
        // would shift the current pointer). The "to" version is the value SaveInternalAsync
        // assigns.
        var fromVersion = target.VersionNumber;

        var saveResult = await SaveInternalAsync(
            application,
            target.FormDataJson,
            ApplicationVersionSource.Revert,
            $"Reverted to version {targetVersionNumber}",
            cancellationToken).ConfigureAwait(false);
        if (saveResult.IsFailure)
        {
            return saveResult;
        }

        // Emit the revert-specific audit row (Notice severity). The plain Save audit row
        // (Information severity) was already emitted by SaveInternalAsync — the two rows
        // together form the full revert trail.
        var details = JsonSerializer.Serialize(new
        {
            applicationSqid = _sqids.Encode(application.Id),
            from = fromVersion,
            to = saveResult.Value.VersionNumber,
        });
        await _audit.RecordAsync(
            eventCode: AuditPrefixReverted,
            severity: AuditSeverity.Notice,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ApplicationVersion),
            targetEntityId: application.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return saveResult;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ApplicationVersionSummaryDto>>> ListAsync(
        string applicationSqid,
        CancellationToken cancellationToken = default)
    {
        var ownership = await ResolveOwnedApplicationAsync(applicationSqid, cancellationToken).ConfigureAwait(false);
        if (ownership.failure is { } failure)
        {
            return Result<IReadOnlyList<ApplicationVersionSummaryDto>>.Failure(failure.code, failure.message);
        }
        var application = ownership.application!;

        var raw = await _db.ApplicationVersions
            .Where(v => v.ServiceApplicationId == application.Id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new
            {
                v.Id,
                v.ServiceApplicationId,
                v.VersionNumber,
                v.CreatedByUserId,
                v.Source,
                v.CreatedAtUtc,
                v.Note,
                v.IsCurrent,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Project + encode in-process — ISqidService is not translatable to SQL.
        var items = raw.Select(r => new ApplicationVersionSummaryDto(
            _sqids.Encode(r.Id),
            _sqids.Encode(r.ServiceApplicationId),
            r.VersionNumber,
            _sqids.Encode(r.CreatedByUserId),
            r.Source.ToString(),
            r.CreatedAtUtc,
            r.Note,
            r.IsCurrent)).ToList();
        return Result<IReadOnlyList<ApplicationVersionSummaryDto>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationVersionOutputDto>> GetAsync(
        string applicationSqid,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        var ownership = await ResolveOwnedApplicationAsync(applicationSqid, cancellationToken).ConfigureAwait(false);
        if (ownership.failure is { } failure)
        {
            return Result<ApplicationVersionOutputDto>.Failure(failure.code, failure.message);
        }
        var application = ownership.application!;

        var row = await _db.ApplicationVersions
            .SingleOrDefaultAsync(
                v => v.ServiceApplicationId == application.Id
                  && v.VersionNumber == versionNumber,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<ApplicationVersionOutputDto>.Failure(ErrorCodes.NotFound, "Version not found.")
            : Result<ApplicationVersionOutputDto>.Success(Project(row));
    }

    /// <summary>
    /// Internal save path that assumes the ownership gate has already passed and the
    /// loaded <see cref="ServiceApplication"/> is available. Handles the editability
    /// check, dedup short-circuit, cap enforcement, current-row flip, INSERT, and the
    /// per-save audit emission.
    /// </summary>
    /// <param name="application">Loaded application (already validated for ownership).</param>
    /// <param name="formDataJson">Form payload to snapshot.</param>
    /// <param name="source">Origin of the save.</param>
    /// <param name="note">Optional annotation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Save outcome.</returns>
    private async Task<Result<ApplicationVersionOutputDto>> SaveInternalAsync(
        ServiceApplication application,
        string formDataJson,
        ApplicationVersionSource source,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!IsEditableFor(application.Status, source))
        {
            return Result<ApplicationVersionOutputDto>.Failure(
                ErrorCodes.ApplicationNotEditable,
                $"Application is in status '{application.Status}' and cannot be versioned.");
        }

        // Caller must be authenticated — required because every version row carries
        // CreatedByUserId. The ownership gate already enforces non-null UserId for the
        // owner branch; managers may still reach here so we re-check.
        if (_caller.UserId is not long actorUserId)
        {
            return Result<ApplicationVersionOutputDto>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Dedup against the current row. The comparison is ordinal because FormDataJson
        // is round-tripped verbatim — a semantically-equivalent but byte-different payload
        // (different key order, different whitespace) is intentionally treated as a new
        // version so the caller's exact intent is preserved.
        var current = await _db.ApplicationVersions
            .Where(v => v.ServiceApplicationId == application.Id && v.IsCurrent)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is not null &&
            string.Equals(current.FormDataJson, formDataJson, StringComparison.Ordinal))
        {
            // No new row written. Counter ticks so operators can chart no-op autosave rate.
            CnasMeter.ApplicationVersionDedup.Add(1);
            await EmitSavedAuditAsync(application.Id, current.VersionNumber, source, deduped: true, cancellationToken).ConfigureAwait(false);
            return Result<ApplicationVersionOutputDto>.Success(Project(current));
        }

        // Cap enforcement BEFORE the insert. We delete the oldest Autosave row first so
        // the SaveChanges call atomically rolls the prune into the same transaction as
        // the insert. Only Autosave rows are eligible — Manual/Submit/Revert are kept.
        if (source == ApplicationVersionSource.Autosave)
        {
            var autosaveCount = await _db.ApplicationVersions
                .CountAsync(
                    v => v.ServiceApplicationId == application.Id
                      && v.Source == ApplicationVersionSource.Autosave,
                    cancellationToken)
                .ConfigureAwait(false);
            if (autosaveCount >= _opts.MaxAutosavesPerApplication)
            {
                var oldest = await _db.ApplicationVersions
                    .Where(v => v.ServiceApplicationId == application.Id
                             && v.Source == ApplicationVersionSource.Autosave)
                    .OrderBy(v => v.VersionNumber)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
                _db.ApplicationVersions.Remove(oldest);
                CnasMeter.ApplicationVersionAutosavePruned.Add(1);
            }
        }

        // Flip the previous current row to historical. We do this on the loaded entity
        // so the SaveChanges call sends both the UPDATE and the INSERT in one transaction;
        // the partial unique index on (ServiceApplicationId, IsCurrent WHERE IsCurrent=true)
        // would reject the insert otherwise.
        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.IsCurrent = false;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var nextVersionNumber = (current?.VersionNumber ?? 0) + 1;
        var row = new ApplicationVersion
        {
            ServiceApplicationId = application.Id,
            VersionNumber = nextVersionNumber,
            FormDataJson = formDataJson,
            CreatedByUserId = actorUserId,
            Source = source,
            Note = note,
            IsCurrent = true,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ApplicationVersions.Add(row);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitSavedAuditAsync(application.Id, row.VersionNumber, source, deduped: false, cancellationToken).ConfigureAwait(false);

        return Result<ApplicationVersionOutputDto>.Success(Project(row));
    }

    /// <summary>
    /// Decodes the Sqid, loads the application, and applies the ownership / management
    /// gate. Returns either a typed failure (Sqid invalid, not found, unauthorised,
    /// forbidden) OR the loaded entity for downstream consumption.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id from the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Failure tuple OR the loaded application.</returns>
    private async Task<(ServiceApplication? application, (string code, string message)? failure)>
        ResolveOwnedApplicationAsync(string applicationSqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(applicationSqid);
        if (decoded.IsFailure)
        {
            return (null, (decoded.ErrorCode!, decoded.ErrorMessage!));
        }

        var application = await _db.Applications
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (application is null)
        {
            return (null, (ErrorCodes.NotFound, "Application not found."));
        }

        if (_caller.UserId is not long callerUserId)
        {
            return (null, (ErrorCodes.Unauthorized, "Caller is not authenticated."));
        }

        var isOwner = application.SolicitantId == callerUserId;
        var isManager = _caller.Roles.Any(r => ManagementRoles.Contains(r, StringComparer.Ordinal));
        if (!isOwner && !isManager)
        {
            return (null, (ErrorCodes.Forbidden, "Not your application."));
        }

        return (application, null);
    }

    /// <summary>
    /// Translates an entity row into the full output DTO with Sqid-encoded identifiers.
    /// Centralised so the encoding rule applies identically across Save / Revert / Get.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The output DTO.</returns>
    private ApplicationVersionOutputDto Project(ApplicationVersion row) => new(
        _sqids.Encode(row.Id),
        _sqids.Encode(row.ServiceApplicationId),
        row.VersionNumber,
        row.FormDataJson,
        _sqids.Encode(row.CreatedByUserId),
        row.Source.ToString(),
        row.CreatedAtUtc,
        row.Note,
        row.IsCurrent);

    /// <summary>
    /// Returns <c>true</c> when the supplied combination of application status and save
    /// source is permitted by the editability gate. Drafts accept any source; Submitted
    /// only accepts the Submit ceremony's own snapshot; anything else is rejected.
    /// </summary>
    /// <param name="status">Current lifecycle state of the application.</param>
    /// <param name="source">Origin of the requested save.</param>
    /// <returns><c>true</c> when the save is allowed.</returns>
    private static bool IsEditableFor(ApplicationStatus status, ApplicationVersionSource source)
        => status switch
        {
            ApplicationStatus.Draft => true,
            ApplicationStatus.Submitted => source == ApplicationVersionSource.Submit,
            _ => false,
        };

    /// <summary>
    /// Emits the per-save audit row. Severity is
    /// <see cref="AuditSeverity.Information"/> because a save is a routine, non-sensitive
    /// event — the citizen is editing their own draft. <c>DetailsJson</c> carries the
    /// application Sqid, version number, source, and dedup flag but NEVER the form
    /// payload (PII guard).
    /// </summary>
    /// <param name="applicationId">Raw FK of the owning application.</param>
    /// <param name="versionNumber">Version number that was saved (or returned via dedup).</param>
    /// <param name="source">Origin of the save (Autosave / ManualSave / Submit / Revert).</param>
    /// <param name="deduped"><c>true</c> when the save short-circuited via the dedup guard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task EmitSavedAuditAsync(
        long applicationId,
        int versionNumber,
        ApplicationVersionSource source,
        bool deduped,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            applicationSqid = _sqids.Encode(applicationId),
            version = versionNumber,
            source = source.ToString(),
            deduped,
        });
        await _audit.RecordAsync(
            eventCode: AuditPrefixSaved,
            severity: AuditSeverity.Information,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ApplicationVersion),
            targetEntityId: applicationId,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
