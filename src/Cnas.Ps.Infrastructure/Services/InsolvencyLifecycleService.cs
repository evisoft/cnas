using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IInsolvencyLifecycleService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Implements R0830 / R0834 / TOR Annex 1
/// §8.1.4.5 — dedicated insolvency lifecycle over the new
/// <c>InsolvencyCase</c> / <c>InsolvencyClaim</c> / <c>InsolvencyPayment</c>
/// triplet, with a transparent fan-out that flips
/// <see cref="Contributor.IsInsolvent"/> in the same atomic save so existing
/// downstream consumers of the bit-flag (search filter, dashboards) keep
/// working.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit payloads are PII-free.</b> Every Critical event emitted by this
/// service carries the raw <c>ContributorId</c>, lifecycle metadata, and the
/// rationale only — the IDNO / IDNP / name fields stay in the encrypted-at-rest
/// <c>Contributor</c> aggregate for clearance-holder access (SEC 044 / CLAUDE.md §5.6).
/// </para>
/// <para>
/// <b>Single open case invariant.</b> The service refuses to open a second
/// concurrent case on the same contributor — operators must resolve the
/// existing one first (or correct it via an admin override outside this
/// surface). This keeps the implicit <c>Contributor.IsInsolvent</c> bit in
/// agreement with the registry.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping.</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller information for audit attribution.</param>
/// <param name="audit">Audit journal façade; Critical events mirror to MLog per SEC 056.</param>
public sealed class InsolvencyLifecycleService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit) : IInsolvencyLifecycleService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    /// <summary>Stable audit event code emitted at successful open.</summary>
    private const string EventOpened = "INSOLVENCY.OPENED";

    /// <summary>Stable audit event code emitted at successful resolution.</summary>
    private const string EventResolved = "INSOLVENCY.RESOLVED";

    /// <summary>Stable audit event code emitted at successful claim registration.</summary>
    private const string EventClaimAdded = "INSOLVENCY.CLAIM_ADDED";

    /// <summary>Stable audit event code emitted at successful payment registration.</summary>
    private const string EventPaymentAdded = "INSOLVENCY.PAYMENT_ADDED";

    /// <summary>Cached JSON options used across audit payload builders (CA1869).</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<Result<InsolvencyCaseDto>> OpenAsync(
        string contributorSqid,
        string reason,
        DateOnly insolvencyDate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        // Validate the shape (reason length + future date) at the service boundary so
        // the controller stays declarative. The clock-anchored validator overload makes
        // the future-date guard deterministic under the injected ICnasTimeProvider.
        var todayUtc = DateOnly.FromDateTime(_clock.UtcNow);
        var validator = new InsolvencyOpenInputValidator(todayUtc);
        var input = new InsolvencyOpenInputDto(contributorSqid, reason, insolvencyDate);
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<InsolvencyCaseDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Result<InsolvencyCaseDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        var contributor = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (contributor is null)
        {
            return Result<InsolvencyCaseDto>.Failure(
                ErrorCodes.NotFound, "Contributor not found.");
        }

        // Single-open invariant — refuse to start a second concurrent case on the same
        // payer. The implicit Contributor.IsInsolvent flag would already be true, so a
        // second open row would diverge from it.
        var alreadyOpen = await _db.InsolvencyCases
            .AnyAsync(c => c.ContributorId == contributorId
                           && c.Status == InsolvencyCaseStatus.Open
                           && c.IsActive,
                      ct)
            .ConfigureAwait(false);
        if (alreadyOpen)
        {
            return Result<InsolvencyCaseDto>.Failure(
                ErrorCodes.Conflict,
                "An open insolvency case already exists for this contributor.");
        }

        var now = _clock.UtcNow;
        var row = new InsolvencyCase
        {
            ContributorId = contributorId,
            InsolvencyDate = insolvencyDate,
            Reason = reason,
            Status = InsolvencyCaseStatus.Open,
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsolvencyCases.Add(row);

        // Fan-out: flip the legacy bit-flag so search filters + dashboards stay coherent.
        contributor.IsInsolvent = true;
        contributor.UpdatedAtUtc = now;
        contributor.UpdatedBy = _caller.UserSqid;

        // iter-149 — TOCTOU defence. The pre-check above runs in a separate read;
        // two concurrent OpenAsync calls could observe "no open case yet" and both
        // proceed to SaveChanges. A unique partial index ("Status = Open") on the
        // contributor column lets Postgres reject the second insert at the
        // constraint layer. We catch the DbUpdateException and translate it to
        // the same stable Conflict code the pre-check returns, so the wire
        // contract is identical regardless of which guard fired.
        // NOTE: InMemory provider doesn't enforce the filter so the pre-check
        // remains the only guard in tests. The partial index is created via the
        // Add..InsolvencyCaseOpenUniqueIndex migration body in production.
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return Result<InsolvencyCaseDto>.Failure(
                ErrorCodes.Conflict,
                "Another open insolvency case already exists for this contributor.");
        }

        await _audit.RecordAsync(
            EventOpened,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(InsolvencyCase),
            row.Id,
            BuildOpenedPayload(row),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<InsolvencyCaseDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> ResolveAsync(
        string caseSqid,
        string resolution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var validator = new InsolvencyResolveInputValidator();
        var validation = validator.Validate(new InsolvencyResolveInputDto(resolution));
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(caseSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.InsolvencyCases
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Insolvency case not found.");
        }

        if (row.Status == InsolvencyCaseStatus.Resolved)
        {
            return Result.Failure(
                ErrorCodes.Conflict, "Insolvency case is already resolved.");
        }

        var contributor = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == row.ContributorId && c.IsActive, ct)
            .ConfigureAwait(false);

        // iter-149 — refuse the resolve when the contributor row is missing. The
        // previous fall-through "mark case Resolved but skip the flag flip" path
        // left the system in a split-brain state (case=Resolved, contributor row
        // either missing or still IsInsolvent=true if the row had been soft-
        // deleted before the flag was cleared). Requiring an active contributor
        // for resolution keeps the registry and the legacy bit-flag in agreement.
        if (contributor is null)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "Cannot resolve insolvency case: the contributor row is missing or inactive.");
        }

        var now = _clock.UtcNow;
        row.Status = InsolvencyCaseStatus.Resolved;
        row.ResolvedAtUtc = now;
        row.Resolution = resolution;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;

        contributor.IsInsolvent = false;
        contributor.UpdatedAtUtc = now;
        contributor.UpdatedBy = _caller.UserSqid;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            EventResolved,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(InsolvencyCase),
            row.Id,
            BuildResolvedPayload(row),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InsolvencyCaseDto>>> ListActiveAsync(
        CancellationToken ct = default)
    {
        var rows = await _db.InsolvencyCases
            .Where(c => c.IsActive && c.Status == InsolvencyCaseStatus.Open)
            .OrderBy(c => c.OpenedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<InsolvencyCaseDto> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<InsolvencyCaseDto>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<InsolvencyClaimDto>> AddClaimAsync(
        string caseSqid,
        InsolvencyClaimInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var todayUtc = DateOnly.FromDateTime(_clock.UtcNow);
        var validator = new InsolvencyClaimInputValidator(todayUtc);
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<InsolvencyClaimDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(caseSqid);
        if (decoded.IsFailure)
        {
            return Result<InsolvencyClaimDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var parent = await _db.InsolvencyCases
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return Result<InsolvencyClaimDto>.Failure(
                ErrorCodes.NotFound, "Insolvency case not found.");
        }
        if (parent.Status == InsolvencyCaseStatus.Resolved)
        {
            return Result<InsolvencyClaimDto>.Failure(
                ErrorCodes.Conflict, "Cannot register a claim on a resolved case.");
        }

        var now = _clock.UtcNow;
        var row = new InsolvencyClaim
        {
            InsolvencyCaseId = parent.Id,
            Amount = input.Amount,
            Currency = input.Currency,
            Description = input.Description,
            IncurredOn = input.IncurredOn,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsolvencyClaims.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            EventClaimAdded,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsolvencyClaim),
            row.Id,
            BuildClaimAddedPayload(row),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<InsolvencyClaimDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InsolvencyClaimDto>>> ListClaimsAsync(
        string caseSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(caseSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<InsolvencyClaimDto>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var caseId = decoded.Value;
        var parentExists = await _db.InsolvencyCases
            .AnyAsync(c => c.Id == caseId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!parentExists)
        {
            return Result<IReadOnlyList<InsolvencyClaimDto>>.Failure(
                ErrorCodes.NotFound, "Insolvency case not found.");
        }

        var rows = await _db.InsolvencyClaims
            .Where(r => r.InsolvencyCaseId == caseId && r.IsActive)
            .OrderBy(r => r.IncurredOn)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<InsolvencyClaimDto> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<InsolvencyClaimDto>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<InsolvencyPaymentDto>> AddPaymentAsync(
        string caseSqid,
        InsolvencyPaymentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var todayUtc = DateOnly.FromDateTime(_clock.UtcNow);
        var validator = new InsolvencyPaymentInputValidator(todayUtc);
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<InsolvencyPaymentDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var decoded = _sqids.TryDecode(caseSqid);
        if (decoded.IsFailure)
        {
            return Result<InsolvencyPaymentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var parent = await _db.InsolvencyCases
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (parent is null)
        {
            return Result<InsolvencyPaymentDto>.Failure(
                ErrorCodes.NotFound, "Insolvency case not found.");
        }
        if (parent.Status == InsolvencyCaseStatus.Resolved)
        {
            return Result<InsolvencyPaymentDto>.Failure(
                ErrorCodes.Conflict, "Cannot register a payment on a resolved case.");
        }

        var now = _clock.UtcNow;
        var row = new InsolvencyPayment
        {
            InsolvencyCaseId = parent.Id,
            Amount = input.Amount,
            PaymentDate = input.PaymentDate,
            Reference = input.Reference,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsolvencyPayments.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            EventPaymentAdded,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsolvencyPayment),
            row.Id,
            BuildPaymentAddedPayload(row),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<InsolvencyPaymentDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InsolvencyPaymentDto>>> ListPaymentsAsync(
        string caseSqid,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(caseSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<InsolvencyPaymentDto>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var caseId = decoded.Value;
        var parentExists = await _db.InsolvencyCases
            .AnyAsync(c => c.Id == caseId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!parentExists)
        {
            return Result<IReadOnlyList<InsolvencyPaymentDto>>.Failure(
                ErrorCodes.NotFound, "Insolvency case not found.");
        }

        var rows = await _db.InsolvencyPayments
            .Where(r => r.InsolvencyCaseId == caseId && r.IsActive)
            .OrderBy(r => r.PaymentDate)
            .ToListAsync(ct).ConfigureAwait(false);

        IReadOnlyList<InsolvencyPaymentDto> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<InsolvencyPaymentDto>>.Success(items);
    }

    // ─────────────────────── Projections + payload builders ───────────────────────

    /// <summary>Maps <see cref="InsolvencyCase"/> to its outbound DTO with Sqid round-tripping.</summary>
    /// <param name="row">Persisted row to project.</param>
    /// <returns>The DTO carrying Sqid strings.</returns>
    private InsolvencyCaseDto Project(InsolvencyCase row) => new(
        Id: _sqids.Encode(row.Id),
        ContributorSqid: _sqids.Encode(row.ContributorId),
        Status: row.Status.ToString(),
        InsolvencyDate: row.InsolvencyDate,
        Reason: row.Reason,
        OpenedAtUtc: row.OpenedAtUtc,
        ResolvedAtUtc: row.ResolvedAtUtc,
        Resolution: row.Resolution);

    /// <summary>Maps <see cref="InsolvencyClaim"/> to its outbound DTO.</summary>
    /// <param name="row">Persisted row to project.</param>
    /// <returns>The DTO carrying Sqid strings.</returns>
    private InsolvencyClaimDto Project(InsolvencyClaim row) => new(
        Id: _sqids.Encode(row.Id),
        InsolvencyCaseSqid: _sqids.Encode(row.InsolvencyCaseId),
        Amount: row.Amount,
        Currency: row.Currency,
        Description: row.Description,
        IncurredOn: row.IncurredOn);

    /// <summary>Maps <see cref="InsolvencyPayment"/> to its outbound DTO.</summary>
    /// <param name="row">Persisted row to project.</param>
    /// <returns>The DTO carrying Sqid strings.</returns>
    private InsolvencyPaymentDto Project(InsolvencyPayment row) => new(
        Id: _sqids.Encode(row.Id),
        InsolvencyCaseSqid: _sqids.Encode(row.InsolvencyCaseId),
        Amount: row.Amount,
        PaymentDate: row.PaymentDate,
        Reference: row.Reference);

    /// <summary>Builds the PII-free audit payload for <see cref="EventOpened"/>.</summary>
    /// <param name="row">Persisted case row.</param>
    /// <returns>JSON literal carrying the lifecycle metadata.</returns>
    private static string BuildOpenedPayload(InsolvencyCase row) =>
        JsonSerializer.Serialize(new
        {
            contributorId = row.ContributorId,
            insolvencyDate = row.InsolvencyDate.ToString("O"),
            reason = row.Reason,
        }, CachedJsonOptions);

    /// <summary>Builds the PII-free audit payload for <see cref="EventResolved"/>.</summary>
    /// <param name="row">Persisted case row after the resolution stamp.</param>
    /// <returns>JSON literal carrying the lifecycle metadata.</returns>
    private static string BuildResolvedPayload(InsolvencyCase row) =>
        JsonSerializer.Serialize(new
        {
            contributorId = row.ContributorId,
            resolvedAtUtc = row.ResolvedAtUtc,
            resolution = row.Resolution,
        }, CachedJsonOptions);

    /// <summary>Builds the PII-free audit payload for <see cref="EventClaimAdded"/>.</summary>
    /// <param name="row">Persisted claim row.</param>
    /// <returns>JSON literal carrying the lifecycle metadata.</returns>
    private static string BuildClaimAddedPayload(InsolvencyClaim row) =>
        JsonSerializer.Serialize(new
        {
            insolvencyCaseId = row.InsolvencyCaseId,
            amount = row.Amount,
            currency = row.Currency,
            incurredOn = row.IncurredOn.ToString("O"),
        }, CachedJsonOptions);

    /// <summary>Builds the PII-free audit payload for <see cref="EventPaymentAdded"/>.</summary>
    /// <param name="row">Persisted payment row.</param>
    /// <returns>JSON literal carrying the lifecycle metadata.</returns>
    private static string BuildPaymentAddedPayload(InsolvencyPayment row) =>
        JsonSerializer.Serialize(new
        {
            insolvencyCaseId = row.InsolvencyCaseId,
            amount = row.Amount,
            paymentDate = row.PaymentDate.ToString("O"),
            reference = row.Reference,
        }, CachedJsonOptions);
}
