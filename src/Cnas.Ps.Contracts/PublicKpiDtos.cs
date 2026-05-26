using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0500 / TOR CF 01.02 — public, anonymous-accessible KPI snapshot. Every
// field is an aggregate count or a system-level timestamp; no row-level
// data, no PII, no business-sensitive volumes beyond depersonalised totals.
// Cached snapshot recomputed at most once every 5 minutes by the service.
//
// Contracts MUST NOT <see cref="…"/> into Cnas.Ps.Core per project rules.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0500 / TOR CF 01.02 — depersonalised KPI snapshot surfaced by
/// <c>GET /api/public/kpis</c>. Every field is a system-wide aggregate.
/// </summary>
/// <param name="ComputedAtUtc">UTC instant the snapshot was computed.</param>
/// <param name="TotalActiveContributors">
/// Count of <c>Contributor</c> rows where <c>IsActive=true</c> and
/// <c>IsDeactivated=false</c>.
/// </param>
/// <param name="TotalActiveInsuredPersons">
/// Count of <c>InsuredPerson</c> rows where <c>IsActive=true</c>.
/// </param>
/// <param name="TotalPendingApplications">
/// Count of <c>ServiceApplication</c> rows in a non-terminal status
/// (<c>Submitted</c>, <c>UnderExamination</c>, or <c>PendingApproval</c>).
/// </param>
/// <param name="DecisionsIssuedLast30Days">
/// Count of <c>ServiceApplication</c> rows that transitioned to
/// <c>Approved</c>, <c>Rejected</c>, or <c>Closed</c> with
/// <c>UpdatedAtUtc</c> within the last 30 days from <paramref name="ComputedAtUtc"/>.
/// </param>
/// <param name="LastSuccessfulTreasuryImportAtUtc">
/// Most recent <c>TreasuryFeedImport.CompletedAt</c> for a row in
/// <c>Completed</c> status; null when no successful import has been
/// recorded yet.
/// </param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record PublicKpiSnapshotDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    System.DateTime ComputedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalActiveContributors,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalActiveInsuredPersons,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalPendingApplications,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long DecisionsIssuedLast30Days,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    System.DateTime? LastSuccessfulTreasuryImportAtUtc);
