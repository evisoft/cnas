namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0920 / TOR BP 2.3-A — labor-booklet master record (Carnet de muncă) registered
/// against a natural-person <see cref="Solicitant"/>. One row per scanned booklet
/// the citizen presents at a CNAS desk; OCR metadata + the scanned binary live on
/// an attached <see cref="AttachmentRecord"/> with
/// <c>OwnerEntityType="LaborBooklet"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner.</b> The booklet belongs to a natural person — modeled in CNAS as a
/// <see cref="Solicitant"/> of kind <see cref="ApplicantKind.NaturalPerson"/>.
/// The <see cref="InsuredPersonSolicitantId"/> FK points at the
/// <see cref="AuditableEntity.Id"/> bigint primary key on the Solicitant. We attach the booklet to the
/// Solicitant rather than the <see cref="InsuredPerson"/> aggregate because the
/// citizen may walk in to register pre-1999 history BEFORE the
/// <see cref="InsuredPerson"/> row has been provisioned (RSP sync, contributions
/// history, ...) — the Solicitant is the lighter-weight identity surface that is
/// always present at the desk.
/// </para>
/// <para>
/// <b>Lifecycle.</b> A freshly-registered booklet sits in
/// <see cref="LaborBookletStatus.Pending"/> until an operator either verifies it
/// (typical happy path) or rejects it (illegible scan, mismatched citizen, ...).
/// Both transitions are terminal — a re-scan starts a fresh row rather than
/// flipping the existing one.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> Within one citizen the same booklet number
/// cannot be registered twice — enforced by the unique index on
/// <c>(InsuredPersonSolicitantId, CarnetMuncaNumber)</c> configured in the EF
/// configuration. Across citizens the same booklet number can recur (the paper
/// archives use locally-unique serial numbers, not globally-unique ones).
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the outbound DTO (<c>Cnas.Ps.Contracts.LaborBookletDto.Id</c>) carries the
/// Sqid-encoded surrogate per CLAUDE.md RULE 3 — operators reference an
/// individual booklet when attaching the scanned copy, verifying, or appending
/// a pre-1999 period row to it.
/// </para>
/// </remarks>
public sealed class LaborBooklet : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the owning <see cref="Solicitant"/> primary key (the natural-person
    /// applicant whose paper booklet is being digitised). Indexed via the unique
    /// composite index on (InsuredPersonSolicitantId, CarnetMuncaNumber).
    /// </summary>
    public long InsuredPersonSolicitantId { get; set; }

    /// <summary>
    /// Booklet serial number typed verbatim from the paper original. Stable identifier
    /// within the citizen; participates in the per-citizen uniqueness constraint.
    /// 1..32 chars matching <c>^[A-Z0-9-]+$</c> (validator at the boundary).
    /// </summary>
    public required string CarnetMuncaNumber { get; set; }

    /// <summary>Issue date printed on the booklet cover, when legible. Optional.</summary>
    public DateOnly? IssuedDate { get; set; }

    /// <summary>
    /// Authority that issued the booklet (e.g. a former employer, a labour
    /// office). Captured verbatim from the booklet's title page when present.
    /// Optional. Capped at 200 chars by the persistence configuration.
    /// </summary>
    public string? IssuingAuthority { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to <see cref="LaborBookletStatus.Pending"/>.
    /// Both <see cref="LaborBookletStatus.Verified"/> and
    /// <see cref="LaborBookletStatus.Rejected"/> are terminal.
    /// </summary>
    public LaborBookletStatus Status { get; set; } = LaborBookletStatus.Pending;

    /// <summary>
    /// R0920 / Annex 2 §8.2.4 — JSON payload carrying the OCR-extracted text +
    /// fields harvested from the scanned booklet (positions, employer names,
    /// dates, ...). Opaque to the server today — the eventual OCR pipeline
    /// (Tesseract / Azure Document Intelligence) is the authoritative producer.
    /// Null on rows that have not yet been augmented with a scanned copy or
    /// whose scanned copy was uploaded without OCR pre-processing.
    /// </summary>
    public string? OcrExtractedJson { get; set; }

    /// <summary>
    /// R0920 / Annex 2 §8.2.4 — categorical confidence band produced by the OCR
    /// pipeline. One of <c>"High"</c>, <c>"Medium"</c>, <c>"Low"</c> (case-
    /// sensitive), or <see langword="null"/> when no OCR took place. The
    /// validator enforces the allow-list at the boundary.
    /// </summary>
    public string? OcrConfidenceLevel { get; set; }

    /// <summary>
    /// Verifier's free-text rationale captured at the time
    /// <c>ILaborBookletService.VerifyAsync</c> succeeds (e.g. "matched against
    /// RSP photo"). Optional. Capped at 500 chars by the persistence configuration.
    /// </summary>
    public string? VerifierNotes { get; set; }

    /// <summary>
    /// FK to the <see cref="UserProfile"/> primary key of the operator that
    /// verified the booklet. Populated only after the row transitions to
    /// <see cref="LaborBookletStatus.Verified"/>.
    /// </summary>
    public long? VerifiedByUserId { get; set; }

    /// <summary>UTC instant the booklet was verified. Set in lock-step with <see cref="VerifiedByUserId"/>.</summary>
    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>
    /// Rejection rationale captured at the time
    /// <c>ILaborBookletService.RejectAsync</c> succeeds. Populated only after
    /// the row transitions to <see cref="LaborBookletStatus.Rejected"/>.
    /// Required when rejecting; capped at 500 chars by the persistence
    /// configuration.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>UTC instant the booklet was rejected.</summary>
    public DateTime? RejectedAtUtc { get; set; }

    /// <summary>
    /// Convenience flag set to <see langword="true"/> the first time
    /// <c>ILaborBookletService.AttachScannedCopyAsync</c> succeeds for the row.
    /// Lets listing endpoints filter on "has a scanned copy" without joining
    /// <see cref="AttachmentRecord"/>. Defaults to <see langword="false"/>.
    /// </summary>
    public bool HasScannedCopy { get; set; }
}
