namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — one review-level decision row
/// attached to an <see cref="IntlAgreementReviewCase"/>. Multiple rows may
/// exist per case — every reviewer interaction (approve, reject, request
/// revision, second review after a revision resubmit) writes a fresh
/// immutable step row so the audit trail is complete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> Steps are never updated in place. The history is
/// ordered by <see cref="AuditableEntity.CreatedAtUtc"/> (mirrored on
/// <see cref="ReviewedAt"/>) so consumers can replay the chain.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class IntlAgreementReviewStep : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="IntlAgreementReviewCase"/>.</summary>
    public long CaseId { get; set; }

    /// <summary>
    /// Routing level at which the decision was made. Only the first three
    /// values of <see cref="IntlAgreementReviewLevel"/> (Local / Regional /
    /// National) ever appear here — Complete / RevisionRequired are
    /// case-level states only.
    /// </summary>
    public IntlAgreementReviewLevel Level { get; set; }

    /// <summary>Reviewer decision at this level.</summary>
    public IntlAgreementReviewStepOutcome Outcome { get; set; }

    /// <summary>UTC timestamp the reviewer recorded the decision.</summary>
    public DateTime ReviewedAt { get; set; }

    /// <summary>FK to the <see cref="UserProfile"/> who recorded the decision.</summary>
    public int ReviewedByUserId { get; set; }

    /// <summary>
    /// Reviewer-supplied note (3..2000 chars). NEVER references the
    /// beneficiary by name or IDNP — only stable codes, dossier refs, and
    /// procedural rationales. Treated as Internal sensitivity.
    /// </summary>
    public required string Note { get; set; }
}
