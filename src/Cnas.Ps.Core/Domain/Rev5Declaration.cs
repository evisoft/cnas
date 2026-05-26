namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0910 / TOR BP 2.2-A — header row for a REV-5 declaration filed by an
/// employer (Plătitor) that breaks the monthly social-insurance contribution
/// down per insured person. One <see cref="Rev5Declaration"/> aggregates many
/// <see cref="Rev5DeclarationRow"/> children — one row per employee covered by
/// the declaration.
/// </summary>
/// <remarks>
/// <para>
/// <b>How REV-5 differs from <see cref="Declaration"/>.</b>
/// The <see cref="Declaration"/> family (R0810-R0812) registers the
/// employer-level contribution figure: one row per (payer × month × source).
/// REV-5 (R0910) instead carries the per-insured-person breakdown that feeds
/// the citizen-facing personal account (R0516). Both registries co-exist —
/// the employer-level aggregate is reconciled against the sum across the
/// REV-5 rows downstream.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> The tuple
/// <c>(FilingContributorId, ReportingMonth, ReferenceNumber)</c> is unique —
/// an employer cannot re-register the same REV-5 reference twice for the
/// same reporting month. Enforced via a unique index in
/// <c>Cnas.Ps.Infrastructure.Persistence.Configurations.Rev5DeclarationConfiguration</c>
/// (and a defensive duplicate probe in the service layer for the InMemory
/// test provider).
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// operators reference the declaration by id when adjusting or cancelling it;
/// the outbound DTO surfaces a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
public sealed class Rev5Declaration : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the filing <see cref="Contributor"/> (the
    /// employer Plătitor that filed the REV-5 declaration). Raw bigint id —
    /// only the outbound DTO surfaces a Sqid-encoded form per CLAUDE.md RULE 3.
    /// </summary>
    public long FilingContributorId { get; set; }

    /// <summary>
    /// Calendar month that the declaration covers. By convention the day
    /// component is always 1 — application-layer code and the validator
    /// enforce <c>Day == 1</c> before persistence.
    /// </summary>
    public DateOnly ReportingMonth { get; set; }

    /// <summary>
    /// UTC instant when the REV-5 declaration was filed (upload time at a
    /// CNAS desk or e-filing timestamp from the employer portal). Distinct
    /// from <see cref="AuditableEntity.CreatedAtUtc"/> which captures the
    /// row-creation instant.
    /// </summary>
    public DateTime FiledAtUtc { get; set; }

    /// <summary>
    /// External reference number assigned by the employer's bookkeeping
    /// system (or the e-filing portal). Required; 1..64 chars enforced by
    /// the validator. Participates in the natural-key uniqueness rule.
    /// </summary>
    public required string ReferenceNumber { get; set; }

    /// <summary>
    /// Lifecycle status — defaults to <see cref="Rev5DeclarationStatus.Received"/>.
    /// Cancelled rows trigger a rollback of the projected
    /// <see cref="PersonalAccountEntry"/> rows for the declaration.
    /// </summary>
    public Rev5DeclarationStatus Status { get; set; } = Rev5DeclarationStatus.Received;

    /// <summary>
    /// Pre-computed sum of <see cref="Rev5DeclarationRow.ContributionAmount"/>
    /// across every active child row. Stored on the header so consumers don't
    /// need to re-aggregate; the service layer recomputes it on insert and on
    /// any per-row adjustment.
    /// </summary>
    public decimal TotalDeclaredAmount { get; set; }

    /// <summary>
    /// Pre-computed count of child rows attached to the declaration. Stored
    /// on the header for the same reason as <see cref="TotalDeclaredAmount"/>.
    /// </summary>
    public int RowCount { get; set; }

    /// <summary>
    /// Optional operator-supplied free-form note (≤ 500 chars when set).
    /// Carries Internal sensitivity at the DTO boundary.
    /// </summary>
    public string? Notes { get; set; }
}
