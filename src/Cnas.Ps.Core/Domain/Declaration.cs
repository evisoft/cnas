namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0810 / R0811 / R0812 — one declaration row attributed to a <see cref="Contributor"/>
/// (Plătitor) in the social-insurance contributions registry. Each row captures the
/// gross contribution declared (or recalculated) for a specific reporting month,
/// regardless of the upstream document family that produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three registration paths land here.</b> The same entity backs three TOR
/// business processes:
/// <list type="bullet">
///   <item>R0810 / BP 1.2-A — automated SFS feed (one row per
///     <see cref="DeclarationKind.Sfs"/>).</item>
///   <item>R0811 / BP 1.2-B — paper declarations submitted at a CNAS desk
///     (<see cref="DeclarationKind.BassFour"/>, <see cref="DeclarationKind.Bass"/>,
///     <see cref="DeclarationKind.BassAn"/>, <see cref="DeclarationKind.Pre2018"/>).</item>
///   <item>R0812 / BP 1.2-C — contributions recalculated from supporting documents
///     (<see cref="DeclarationKind.Control"/>, <see cref="DeclarationKind.CourtDecision"/>,
///     <see cref="DeclarationKind.Other"/>).</item>
/// </list>
/// The <see cref="MonthlyContributionCalculation"/> aggregate (R0813 / BP 1.2-D)
/// rolls every non-cancelled row up to a single per-payer per-month figure.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> When <see cref="ReferenceNumber"/> is supplied
/// the tuple <c>(ContributorId, Kind, ReportingMonth, ReferenceNumber)</c> is
/// unique via a filtered index, so the same external reference number cannot be
/// re-registered for the same payer / kind / month. When the reference is null
/// no uniqueness is enforced (a payer may file multiple control-derived
/// adjustments against the same month with different rationales). Both paths are
/// configured in
/// <c>Cnas.Ps.Infrastructure.Persistence.Configurations.DeclarationConfiguration</c>.
/// </para>
/// <para>
/// <b>Adjustment workflow.</b> Operators may post a correction by transitioning a
/// row to <see cref="DeclarationStatus.Adjusted"/> via
/// <c>IDeclarationService.AdjustAsync</c>; the original
/// <see cref="DeclaredContributionAmount"/> is preserved verbatim (the audit
/// snapshot rule from CLAUDE.md cross-cutting "Immutable Snapshots") and the
/// supersession amount lives in <see cref="AdjustedContributionAmount"/>. The
/// monthly aggregator prefers the adjusted figure when present.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.DeclarationDto.Id</c>) carries the
/// Sqid-encoded surrogate per CLAUDE.md RULE 3 — operators reference an
/// individual row when challenging or annotating it.
/// </para>
/// </remarks>
public sealed class Declaration : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the owning <see cref="Contributor"/> (Plătitor)
    /// that filed (or had filed against them) this declaration row.
    /// </summary>
    public long ContributorId { get; set; }

    /// <summary>
    /// Origin of the row — drives the natural-key shape and the audit-event-code
    /// suffix. Persisted as <c>int</c> via the EF configuration.
    /// </summary>
    public DeclarationKind Kind { get; set; }

    /// <summary>
    /// Calendar month that the declaration covers. By convention the day
    /// component is always 1 — the canonical "first of the month" anchor that
    /// the monthly aggregator (R0813) windows against. Application-layer code
    /// (and the validator) enforce <c>Day == 1</c> before persistence; the
    /// schema does not enforce it because PostgreSQL has no native month-only
    /// type.
    /// </summary>
    public DateOnly ReportingMonth { get; set; }

    /// <summary>
    /// UTC instant when the declaration was filed (SFS push timestamp,
    /// CNAS desk submission time, or document date for control / court rows).
    /// Distinct from <see cref="AuditableEntity.CreatedAtUtc"/> which captures
    /// the row-creation timestamp (often later — e.g. a back-dated paper form
    /// captured the next business day).
    /// </summary>
    public DateTime FiledAtUtc { get; set; }

    /// <summary>
    /// External reference number assigned by the upstream system (SFS document
    /// id, CNAS form serial, control-report number, ...). Optional — some legacy
    /// paths have no reference. When non-null it participates in the filtered
    /// unique index.
    /// </summary>
    public string? ReferenceNumber { get; set; }

    /// <summary>
    /// Gross contribution amount the declaration reports (MDL). Bounded
    /// 0 ≤ x ≤ 100_000_000 by the validator at the boundary; no upper bound at
    /// the schema level (the precision is 18,2).
    /// </summary>
    public decimal DeclaredContributionAmount { get; set; }

    /// <summary>
    /// Supersession amount populated when an operator later corrects the
    /// declared figure via <c>IDeclarationService.AdjustAsync</c>. Null while
    /// the row carries the original declaration unchanged; populated when the
    /// row's <see cref="Status"/> is <see cref="DeclarationStatus.Adjusted"/>.
    /// The monthly aggregator (R0813) prefers this value over
    /// <see cref="DeclaredContributionAmount"/> when present.
    /// </summary>
    public decimal? AdjustedContributionAmount { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to <see cref="DeclarationStatus.Received"/>.
    /// Cancelled rows are excluded from R0813 monthly totals; every other
    /// status is included.
    /// </summary>
    public DeclarationStatus Status { get; set; } = DeclarationStatus.Received;

    /// <summary>
    /// Operator-supplied free-form note (3..500 chars when set) — typically the
    /// adjustment rationale or the cancellation reason. Carries Internal
    /// sensitivity at the DTO boundary.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Soft-archive flag distinct from <see cref="AuditableEntity.IsActive"/>:
    /// archived rows remain visible to lookups and the audit trail but are
    /// excluded from the operational listings. <c>IsActive=false</c> is the
    /// CLAUDE.md soft-delete; <see cref="IsArchived"/> is the long-term cold
    /// flag used after a reporting period closes.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// R0821 / R0823 / Annex 1 §8.1.3 — JSON payload carrying the OCR-extracted
    /// text + numeric fields harvested from the scanned paper declaration
    /// (declaration form code, payer IDNO, declared totals, ...). Capped to
    /// 100 000 chars by the input validator. Null on rows that have not yet
    /// been augmented with a scanned copy or whose scanned copy was uploaded
    /// without OCR pre-processing.
    /// </summary>
    /// <remarks>
    /// The shape of the JSON is opaque to the server (no schema enforced here);
    /// the eventual OCR pipeline (Tesseract / Azure Document Intelligence) is
    /// the authoritative producer. Today the field accepts an opaque blob from
    /// the caller as a forward-compatible placeholder; downstream consumers
    /// MUST tolerate unknown fields. Full-text search over this column is
    /// deferred to the R0522 ElasticSearch path.
    /// </remarks>
    public string? OcrExtractedJson { get; set; }

    /// <summary>
    /// R0821 / R0823 / Annex 1 §8.1.3 — categorical confidence band produced
    /// by the OCR pipeline. One of <c>"High"</c>, <c>"Medium"</c>, <c>"Low"</c>
    /// (case-sensitive), or <see langword="null"/> when no OCR took place. The
    /// validator enforces the allow-list — unknown values are refused before
    /// the row is persisted.
    /// </summary>
    public string? OcrConfidenceLevel { get; set; }

    /// <summary>
    /// R0821 / R0823 / Annex 1 §8.1.3 — convenience flag set to
    /// <see langword="true"/> the first time
    /// <c>IDeclarationService.AttachScannedCopyAsync</c> succeeds for the row.
    /// Lets the explorer endpoint (R0822) and downstream reports filter on
    /// "has a scanned copy" without joining
    /// <c>AttachmentRecord</c>. Defaults to <see langword="false"/>.
    /// </summary>
    public bool HasScannedCopy { get; set; }

    /// <summary>
    /// R0823 / Annex 1 §8.1.3 — code of the CNAS branch / office that
    /// registered the declaration (mirrors
    /// <c>Cnas.Ps.Core.Domain.CnasBranch.Code</c>). Optional today —
    /// historic SFS-feed rows carry no office attribution; CNAS-desk rows
    /// (R0811) populate it once the desk-clerk flow lands. Max 32 chars.
    /// </summary>
    public string? RegisteredByOffice { get; set; }

    /// <summary>
    /// R0823 / Annex 1 §8.1.3 — version identifier of the declaration form the
    /// row was filed against (e.g. <c>"4-BASS-v2018"</c>, <c>"BASS-AN-v2021"</c>,
    /// <c>"SFS-IPC18"</c>). Optional — historic rows ingested before the form
    /// catalogue landed leave this null. Max 32 chars.
    /// </summary>
    public string? FormVersion { get; set; }
}
