using System.Diagnostics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>UC06 — Submit application (Cerere). Owns the Draft → Submitted transition.</summary>
/// <remarks>
/// <para>
/// Optionally honours a delegated submission when the caller supplies
/// <see cref="SubmitApplicationInput.OnBehalfOfPrincipalIdnp"/>: the MPass-issued
/// <c>mpower:principal_idnp</c> claim on <see cref="ICallerContext"/> must match the
/// requested principal (case-insensitive) before the application is persisted against
/// the principal's Solicitant record. MPower is consumed indirectly via MPass — NOT as
/// a separate HTTP endpoint — so the check is a pure in-memory comparison against the
/// SAML-derived claim set. See TOR §2.5, UC06 CF 06.02, R0551, and
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MPower".
/// </para>
/// <para>
/// On successful submission the service also publishes a <see cref="MCabinetStatus.Submitted"/>
/// citizen-portal card to MCabinet so the applicant sees the new dossier event in their
/// unified government dashboard. The publish is best-effort: a failure (transport error,
/// non-2xx response, or the publisher throwing) is logged at <c>Warning</c> level and
/// swallowed so the dossier state change — the source of truth — always commits. See
/// <see cref="IMCabinetPublisher"/> and CLAUDE.md cross-cutting "Idempotent Callbacks".
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping (CLAUDE.md RULE 3).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller — supplies <c>UserSqid</c>, roles, IP, and MPower delegation claims.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="notify">Citizen-notification enqueuer.</param>
/// <param name="mcabinet">Citizen-portal publisher (MCabinet) — best-effort outbound projection.</param>
/// <param name="logger">Structured logger for the best-effort publish path.</param>
/// <param name="idHasher">
/// Deterministic HMAC hasher used to look up Solicitants by IDNP via the
/// <see cref="Solicitant.NationalIdHash"/> shadow column. Equality lookups against the
/// encrypted plaintext <c>NationalId</c> column cannot work because each row encrypts
/// to a different ciphertext (random GCM nonce).
/// </param>
/// <param name="examinerAssignment">
/// R0570 / TOR CF 08.02 — distribution-of-incoming-cases service used by
/// <see cref="SubmitAsync"/> to pick the next examiner under the round-robin
/// + registrar-exclusion policy. A failure to find an eligible examiner
/// surfaces as <c>APPLICATION.NO_AVAILABLE_EXAMINER</c> and aborts the
/// submission BEFORE any cerere row is persisted.
/// </param>
/// <param name="autoCreator">
/// R0540 / TOR CF 05.01 (iter 134) — optional rule-driven workflow-task
/// auto-creator. When supplied (production wiring), every successful Draft →
/// Submitted transition fires the configured <c>WorkflowAutoCreationRule</c>
/// rows and stages the resulting <see cref="WorkflowTask"/> rows atomically
/// alongside the application row. Legacy test compositions that pre-date the
/// wiring leave this parameter at its <c>null</c> default — the submit path
/// then skips the auto-creation step.
/// </param>
/// <param name="statusGuard">
/// R0939 / iter 136 — optional centralised application-status guard. When
/// supplied (production wiring), <see cref="WithdrawAsync"/> consults the pinned
/// 8-state transition matrix instead of the hand-rolled <c>if</c>-ladder. Legacy
/// test compositions that pre-date the wiring leave this parameter at its
/// <c>null</c> default — the original ladder is preserved as the fallback, and
/// the <c>APPLICATION.LOCKED</c> error code is unchanged across both paths.
/// </param>
public sealed class ApplicationServiceImpl(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    INotificationService notify,
    IMCabinetPublisher mcabinet,
    ILogger<ApplicationServiceImpl> logger,
    IDeterministicHasher idHasher,
    IExaminerAssignmentService examinerAssignment,
    Cnas.Ps.Application.Workflow.IWorkflowTaskAutoCreator? autoCreator = null,
    IApplicationStatusGuard? statusGuard = null) : IApplicationService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly INotificationService _notify = notify;
    private readonly IMCabinetPublisher _mcabinet = mcabinet;
    private readonly ILogger<ApplicationServiceImpl> _logger = logger;
    private readonly IDeterministicHasher _idHasher = idHasher;
    private readonly IExaminerAssignmentService _examinerAssignment = examinerAssignment;

    /// <summary>
    /// R0939 / iter 136 — optional centralised application-status guard. When
    /// supplied (production wiring), <see cref="WithdrawAsync"/> consults the pinned
    /// 8-state matrix instead of the legacy hand-rolled <c>if</c>-ladder. Legacy test
    /// compositions that pre-date the wiring leave this parameter at its <c>null</c>
    /// default — the legacy ladder is preserved as the fallback for backward
    /// compatibility, and the <c>APPLICATION.LOCKED</c> error code is unchanged.
    /// </summary>
    private readonly IApplicationStatusGuard? _statusGuard = statusGuard;
    /// <summary>
    /// R0540 / TOR CF 05.01 (iter 134) — rule-driven workflow-task auto-creator.
    /// Optional: legacy test compositions that pre-date the wiring construct the
    /// service without it. When supplied, every successful Draft → Submitted
    /// transition emits the configured workflow tasks atomically.
    /// </summary>
    private readonly Cnas.Ps.Application.Workflow.IWorkflowTaskAutoCreator? _autoCreator = autoCreator;

    /// <inheritdoc />
    public async Task<Result<ApplicationOutput>> SubmitAsync(SubmitApplicationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<ApplicationOutput>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        var passportDecoded = _sqids.TryDecode(input.ServicePassportId);
        if (passportDecoded.IsFailure)
        {
            return Result<ApplicationOutput>.Failure(passportDecoded.ErrorCode!, passportDecoded.ErrorMessage!);
        }

        var passport = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Id == passportDecoded.Value && p.IsActive && p.IsEnabled, cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            return Result<ApplicationOutput>.Failure(ErrorCodes.NotFound, "Service passport not found or disabled.");
        }

        var solicitant = await _db.Solicitants
            .SingleOrDefaultAsync(s => s.Id == userId.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            return Result<ApplicationOutput>.Failure(ErrorCodes.NotFound, "Solicitant profile missing.");
        }

        // ── Delegated submission (UC06 CF 06.02, R0551) ────────────────────────────────
        // When the caller specifies OnBehalfOfPrincipalIdnp, the authenticated operator
        // (delegate) must hold a valid MPower power of attorney for the principal and the
        // requested service code. On success, the application is persisted against the
        // PRINCIPAL's Solicitant record — not the operator's — so the cerere is owned by
        // the represented citizen.
        if (!string.IsNullOrWhiteSpace(input.OnBehalfOfPrincipalIdnp))
        {
            // MPower delegation is now claim-based — the SAML assertion from MPass carries
            // an OnBehalfOfPrincipalIdnp claim populated only if the citizen authorised the
            // operator through the MPower portal. We verify the requested principal matches
            // the claim (case-insensitive IDNP comparison).
            var claimPrincipal = _caller.OnBehalfOfPrincipalIdnp;
            if (string.IsNullOrWhiteSpace(claimPrincipal) ||
                !string.Equals(claimPrincipal, input.OnBehalfOfPrincipalIdnp, StringComparison.OrdinalIgnoreCase))
            {
                return Result<ApplicationOutput>.Failure(
                    ErrorCodes.MPowerNotAuthorized,
                    "Delegation not on file.");
            }

            // Resolve the principal Solicitant — MPower can confirm the delegation, but
            // the application still needs a local Solicitant row to associate with.
            // Lookup goes through the NationalIdHash shadow column because NationalId is
            // encrypted at rest (different ciphertext per row → no SQL equality match).
            // See Solicitant.NationalId / NationalIdHash XML doc for the contract.
            var principalIdnpHash = _idHasher.ComputeHash(input.OnBehalfOfPrincipalIdnp);
            var principal = await _db.Solicitants
                .SingleOrDefaultAsync(s => s.NationalIdHash == principalIdnpHash && s.IsActive, cancellationToken)
                .ConfigureAwait(false);
            if (principal is null)
            {
                return Result<ApplicationOutput>.Failure(ErrorCodes.NotFound, "Principal solicitant not registered.");
            }

            // The application is created on behalf of the principal — swap the owning
            // Solicitant from the operator to the principal before persistence.
            solicitant = principal;
        }

        var now = _clock.UtcNow;
        var refNum = $"PS-{now:yyyyMMdd}-{Guid.NewGuid():N}".Substring(0, 32);

        // R0129 / R0142 / CF 15.04 — pin the application to the workflow + passport
        // versions that are CURRENT at submission. The passport FK already points at the
        // specific version row (the lookup above filtered by IsActive without filtering
        // by IsCurrent, but a republish flips IsCurrent on the prior row without breaking
        // the FK). We additionally materialise the version number so reports and
        // diagnostic dashboards can join without re-resolving the FK target. The pinned
        // workflow version is the current revision for the passport's WorkflowCode at
        // submission time; if no workflow row exists yet the value defaults to 1 so
        // an unconfigured pilot environment still round-trips.
        var pinnedWorkflowVersion = await _db.WorkflowDefinitions
            .Where(w => w.Code == passport.WorkflowCode && w.IsCurrent)
            .Select(w => (int?)w.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 1;

        // R0570 / TOR CF 08.02 — distribute the incoming cerere to the next
        // examiner via the round-robin service. CF 08.02 forbids the same
        // person registering AND examining the cerere — the assignment
        // service excludes the registrar (the authenticated caller) from
        // the candidate pool. If no eligible examiner remains the
        // submission MUST NOT proceed; the controller surfaces the
        // failure as 409 ProblemDetails.
        //
        // The assignment runs BEFORE the application is added so a missing
        // examiner pool never strands a half-persisted cerere. The
        // assignment service persists the cursor in its own SaveChanges
        // scope; on a downstream failure the cursor bump shifts the
        // rotation by one slot which is benign (uniform spread is
        // preserved over the long run).
        var assignResult = await _examinerAssignment
            .AssignExaminerAsync(applicationId: 0, registrarUserId: userId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (assignResult.IsFailure)
        {
            return Result<ApplicationOutput>.Failure(
                assignResult.ErrorCode!, assignResult.ErrorMessage!);
        }

        var app = new ServiceApplication
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.Submitted,
            FormPayloadJson = input.FormPayloadJson,
            SnapshotJson = "{}",
            SubmittedAtUtc = now,
            ReferenceNumber = refNum,
            PinnedServicePassportVersion = passport.Version,
            PinnedWorkflowVersion = pinnedWorkflowVersion,
            AssignedExaminerUserId = assignResult.Value,
        };
        _db.Applications.Add(app);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // R0540 / CF 05.01 (iter 134) — rule-driven auto-task creation. Fires for
        // every configured (Draft, Submitted) rule; the auto-creator stages rows
        // on the change tracker and we flush them in a follow-up SaveChanges so
        // the dossier reference (still null at submit time) is set lazily. A
        // failure is logged + swallowed so the submission itself, which has
        // already committed, is not reverted.
        if (_autoCreator is not null)
        {
            try
            {
                var autoResult = await _autoCreator.OnApplicationTransitionAsync(
                    app.Id, ApplicationStatus.Draft, ApplicationStatus.Submitted, cancellationToken)
                    .ConfigureAwait(false);
                if (autoResult.IsSuccess && autoResult.Value!.Count > 0)
                {
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (autoResult.IsFailure)
                {
                    _logger.LogWarning(
                        "Workflow task auto-creation reported failure on submit: applicationId={ApplicationId} errorCode={ErrorCode}",
                        app.Id, autoResult.ErrorCode);
                }
            }
#pragma warning disable CA1031 // Auto-creator failure must NOT roll back the committed submission.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Workflow task auto-creation threw on submit: applicationId={ApplicationId}",
                    app.Id);
            }
#pragma warning restore CA1031
        }

        // If the submission used a delegation, include the MPower delegation id in the
        // audit JSON so an investigator can correlate this dossier back to the underlying
        // power-of-attorney record on the MPower side.
        var auditDetails = string.IsNullOrEmpty(_caller.DelegationPowerId)
            ? $"{{\"refNum\":\"{refNum}\"}}"
            : $"{{\"refNum\":\"{refNum}\",\"delegationPowerId\":\"{_caller.DelegationPowerId}\"}}";
        await _audit.RecordAsync(
            "APPLICATION.SUBMITTED", AuditSeverity.Notice,
            _caller.UserSqid ?? "?", nameof(ServiceApplication), app.Id,
            auditDetails, _caller.SourceIp, _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        await _notify.EnqueueAsync(
            solicitant.Id,
            "Cererea Dvs. a fost înregistrată",
            $"Numărul de referință: {refNum}",
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        // ── MCabinet outbound projection (best-effort, CLAUDE.md "Idempotent Callbacks") ─
        // The application has been persisted; the dossier proper is created downstream by
        // ApplicationProcessingService.AdvanceAsync. At submission time we publish under
        // the application's Sqid — it is the stable external identifier the citizen sees
        // first. MCabinet de-duplicates by (systemCode, externalId), so a later card
        // revision keyed off the dossier Sqid will produce a separate entry — that is the
        // intended granularity (Submitted vs. dossier-bound events).
        var applicationSqid = _sqids.Encode(app.Id);
        var card = new MCabinetCard(
            ExternalId: applicationSqid,
            CitizenIdnp: solicitant.NationalId,
            ServiceCode: passport.Code,
            Status: MCabinetStatus.Submitted,
            TitleRo: passport.NameRo,
            SubtitleRo: refNum,
            EventUtc: now,
            DeepLink: null);
        await PublishMCabinetAsync(card, cancellationToken).ConfigureAwait(false);

        return Result<ApplicationOutput>.Success(new ApplicationOutput(
            applicationSqid,
            app.Status.ToString(),
            app.ReferenceNumber,
            app.SubmittedAtUtc));
    }

    /// <summary>
    /// Publishes the supplied <paramref name="card"/> to MCabinet, swallowing every error
    /// at the boundary so the dossier state machine cannot be broken by an outbound
    /// projection failure. Transport / 5xx / publisher-throws are all logged at
    /// <c>Warning</c> level with structured fields <c>dossierSqid</c> and <c>status</c>;
    /// publish success is logged at <c>Debug</c> level only.
    /// </summary>
    /// <param name="card">Card revision to publish.</param>
    /// <param name="cancellationToken">Cancellation propagation token.</param>
    private async Task PublishMCabinetAsync(MCabinetCard card, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mcabinet.PublishCardAsync(card, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MCabinet publish failed: dossierSqid={DossierSqid} status={Status} errorCode={ErrorCode}",
                    card.ExternalId, card.Status, result.ErrorCode);
            }
        }
#pragma warning disable CA1031 // Best-effort projection: a thrown publisher MUST NOT break the dossier state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MCabinet publish threw: dossierSqid={DossierSqid} status={Status}",
                card.ExternalId, card.Status);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ApplicationListItemOutput>>> MineAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = _caller.UserId;
        if (userId is null)
        {
            return Result<PagedResult<ApplicationListItemOutput>>.Failure(ErrorCodes.Unauthorized, "Not authenticated.");
        }

        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var skip = Math.Max(0, page.Page - 1) * pageSize;

        var query = _db.Applications.Where(a => a.SolicitantId == userId.Value && a.IsActive).OrderByDescending(a => a.CreatedAtUtc);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .Skip(skip).Take(pageSize)
            .Select(a => new { a.Id, a.Status, a.ReferenceNumber, a.SolicitantId, a.CreatedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = items.Select(a => new ApplicationListItemOutput(
            _sqids.Encode(a.Id),
            a.Status.ToString(),
            a.ReferenceNumber,
            _sqids.Encode(a.SolicitantId),
            a.CreatedAtUtc)).ToList();

        return Result<PagedResult<ApplicationListItemOutput>>.Success(new PagedResult<ApplicationListItemOutput>(rows, page.Page, pageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationOutput>> GetAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(applicationId);
        if (decoded.IsFailure)
        {
            return Result<ApplicationOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.Applications
            .Where(a => a.Id == decoded.Value && a.IsActive)
            .Select(a => new { a.Id, a.Status, a.ReferenceNumber, a.SubmittedAtUtc, a.SolicitantId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result<ApplicationOutput>.Failure(ErrorCodes.NotFound, "Application not found.");
        }

        if (_caller.UserId is null || row.SolicitantId != _caller.UserId.Value)
        {
            // Authorisation: applicants see only their own; CNAS staff handled by other endpoints.
            if (!_caller.Roles.Contains("cnas-user"))
            {
                return Result<ApplicationOutput>.Failure(ErrorCodes.Forbidden, "Not your application.");
            }
        }

        return Result<ApplicationOutput>.Success(new ApplicationOutput(
            _sqids.Encode(row.Id),
            row.Status.ToString(),
            row.ReferenceNumber,
            row.SubmittedAtUtc));
    }

    /// <inheritdoc />
    /// <remarks>
    /// On successful withdrawal — the solicitant-initiated terminal transition that does
    /// NOT pass through Approved / Rejected — the service publishes a
    /// <see cref="MCabinetStatus.Closed"/> citizen-portal card under the application Sqid
    /// so MCabinet treats it as an update of the original Submitted card (idempotent on
    /// <c>(systemCode, externalId)</c>). The publish is best-effort: a transport / non-2xx
    /// / publisher-throws failure is logged at <c>Warning</c> level and swallowed so the
    /// withdraw transition (the source of truth) commits regardless. See CLAUDE.md
    /// cross-cutting "Idempotent Callbacks".
    /// </remarks>
    public async Task<Result> WithdrawAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(applicationId);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // Eager-load the Solicitant + ServicePassport navs so the MCabinet card can be
        // built from in-memory data without a second round-trip after the SaveChanges call.
        var app = await _db.Applications
            .Include(a => a.Solicitant)
            .SingleOrDefaultAsync(a => a.Id == decoded.Value && a.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (app is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Application not found.");
        }
        if (app.SolicitantId != _caller.UserId)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Not your application.");
        }

        // R0939 / iter 136 — when the centralised status guard is wired, consult the
        // pinned 8-state matrix BEFORE the legacy ladder. The matrix knows that
        // Withdrawn is reachable from {Draft, Submitted, RejectedIncomplete,
        // UnderExamination} only; PendingApproval / SignedByDirector / Approved /
        // Rejected / Closed / Returned are all locked. To preserve the legacy
        // ErrorCodes.ApplicationLocked surface (consumed by HTTP mapping +
        // existing test pins) we DOWNGRADE the guard's APPLICATION.ILLEGAL_TRANSITION
        // verdict to APPLICATION.LOCKED with the same diagnostic message — the wire
        // contract is unchanged. Legacy test compositions that leave the guard at
        // null fall through to the hand-rolled ladder unchanged.
        if (_statusGuard is not null)
        {
            var verdict = await _statusGuard
                .ValidateTransitionAsync(app.Id, ApplicationStatus.Withdrawn, cancellationToken)
                .ConfigureAwait(false);
            if (verdict.IsFailure)
            {
                // NotFound is impossible here — we just loaded the row — but keep the
                // mapping defensive so a future race-window can never propagate the
                // wrong error code to the citizen.
                if (verdict.ErrorCode == ErrorCodes.NotFound)
                {
                    return Result.Failure(ErrorCodes.NotFound, verdict.ErrorMessage!);
                }
                return Result.Failure(ErrorCodes.ApplicationLocked, "Application already final.");
            }
        }
        else if (app.Status is ApplicationStatus.Closed or ApplicationStatus.Approved or ApplicationStatus.Rejected)
        {
            // Legacy ladder retained for null-guard test compositions. The guard wiring
            // above subsumes this branch in production.
            return Result.Failure(ErrorCodes.ApplicationLocked, "Application already final.");
        }

        var now = _clock.UtcNow;
        app.Status = ApplicationStatus.Withdrawn;
        app.ClosedAtUtc = now;
        app.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            "APPLICATION.WITHDRAWN", AuditSeverity.Notice,
            _caller.UserSqid ?? "?", nameof(ServiceApplication), app.Id,
            "{}", _caller.SourceIp, _caller.CorrelationId, cancellationToken).ConfigureAwait(false);

        // ── MCabinet outbound projection (best-effort, CLAUDE.md "Idempotent Callbacks") ─
        // Withdrawal is the solicitant-initiated terminal transition that bypasses
        // Approved / Rejected. We mirror a Closed card revision keyed off the same
        // application Sqid that the Submitted card used so MCabinet treats this as an
        // update of the existing dashboard entry rather than a separate one. The
        // ServicePassport has no navigation property on ServiceApplication so we fetch
        // the two columns we need (Code, NameRo) by FK.
        var passport = await _db.ServicePassports.AsNoTracking()
            .Where(p => p.Id == app.ServicePassportId)
            .Select(p => new { p.Code, p.NameRo })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (passport is not null && app.Solicitant is not null)
        {
            var card = new MCabinetCard(
                ExternalId: _sqids.Encode(app.Id),
                CitizenIdnp: app.Solicitant.NationalId,
                ServiceCode: passport.Code,
                Status: MCabinetStatus.Closed,
                TitleRo: passport.NameRo,
                SubtitleRo: app.ReferenceNumber,
                EventUtc: now,
                DeepLink: null);
            await PublishMCabinetAsync(card, cancellationToken).ConfigureAwait(false);
        }

        // OTel metric (best-effort) — citizen-initiated withdrawal is counted in the
        // same rejected counter as decider/examiner rejections so the rolling
        // approval-rate calculation captures every non-approved terminal state. The
        // "withdrawn" tag lets dashboards split out the citizen-initiated subset;
        // service_code adds the dossier-level dimension. Passport may have been
        // hard-deleted (rare) — fall back to "?" so the count still flows.
        RecordCounterSafelyMulti(
            CnasTelemetry.DossiersRejected,
            new TagList
            {
                { "tag", "withdrawn" },
                { "service_code", passport?.Code ?? "?" },
            });

        return Result.Success();
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="System.Diagnostics.Metrics.Counter{T}"/>.Add
    /// that swallows any exception thrown by a downstream
    /// <see cref="System.Diagnostics.Metrics.MeterListener"/> or exporter. The dossier
    /// state machine must never be broken by a telemetry side-effect — mirrors the
    /// MCabinet best-effort pattern used in <see cref="PublishMCabinetAsync"/>.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tag">Key/value tag describing the dossier dimension.</param>
    private void RecordCounterSafely(
        System.Diagnostics.Metrics.Counter<long> counter,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            counter.Add(1, tag);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort multi-tag counter increment. Used when more than one dimension
    /// is meaningful (e.g. "tag" describing the rejection channel <i>and</i>
    /// "service_code" describing the underlying service). Any thrown exception is
    /// logged at <c>Warning</c> level and swallowed.
    /// </summary>
    /// <param name="counter">Pre-declared counter to increment by one.</param>
    /// <param name="tags">Set of key/value tags to attach to the measurement.</param>
    private void RecordCounterSafelyMulti(
        System.Diagnostics.Metrics.Counter<long> counter,
        TagList tags)
    {
        try
        {
            counter.Add(1, tags);
        }
#pragma warning disable CA1031 // Best-effort telemetry: a misbehaving listener MUST NOT break the state machine.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Telemetry counter {Counter} increment threw; ignoring.",
                counter.Name);
        }
#pragma warning restore CA1031
    }
}
