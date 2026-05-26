using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0332 / TOR CF 12.02 — electronic archive metadata DTOs. Surfaced by the
// IArchiveMetadataService through GET /api/archive/summary and consumed by
// the tabbed Web UI at /archive. The summary is depersonalised (counts only,
// no row contents) so it can be cached across operators and rendered as a
// header strip above each per-tab list.
//
// Sensitivity: Internal — operator-facing only, never published to anonymous
// or public surfaces. Contracts MUST NOT <see cref="…"/> into Cnas.Ps.Core
// per project rules.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0332 / TOR CF 12.02 — depersonalised counts for one archive tab
/// (Contributors / Insured Persons / Decisions / Dossiers / Documents).
/// The tab UI binds Active / Archived chips + the LastUpdatedUtc badge to
/// these three numbers.
/// </summary>
/// <param name="TabCode">
/// Stable tab discriminator (<c>contributors</c>, <c>insured-persons</c>,
/// <c>decisions</c>, <c>dossiers</c>, <c>documents</c>). Kept short so URLs
/// remain readable.
/// </param>
/// <param name="TotalActive">Count of rows with <c>IsActive=true</c>.</param>
/// <param name="TotalArchived">Count of rows with <c>IsActive=false</c> (soft-deleted).</param>
/// <param name="LastUpdatedUtc">
/// Most recent <c>UpdatedAtUtc</c> (fall back to <c>CreatedAtUtc</c>) across
/// the tab's underlying register, or <c>null</c> when the register is empty.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ArchiveTabSummaryDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TabCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalActive,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    long TotalArchived,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    System.DateTime? LastUpdatedUtc);

/// <summary>
/// R0332 / TOR CF 12.02 — one-shot archive summary used by the Web UI to
/// render the metadata chips above every tab. The five tabs map to the five
/// register types the TOR §2.3 lists as part of the electronic archive:
/// Contributors, Insured Persons, Decisions, Dossiers, Documents.
/// </summary>
/// <param name="Contributors">Counts for Annex 1 — <c>Plătitori de contribuții</c>.</param>
/// <param name="InsuredPersons">Counts for Annex 2 — <c>Persoane asigurate</c>.</param>
/// <param name="Decisions">Counts for issued Decision documents (<c>DocumentKind.Decision</c>).</param>
/// <param name="Dossiers">Counts for the dossier (Dosar) register.</param>
/// <param name="Documents">Counts for the broader Document register (all kinds).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record ArchiveSummaryDto(
    ArchiveTabSummaryDto Contributors,
    ArchiveTabSummaryDto InsuredPersons,
    ArchiveTabSummaryDto Decisions,
    ArchiveTabSummaryDto Dossiers,
    ArchiveTabSummaryDto Documents)
{
    /// <summary>
    /// Convenience accessor — every tab summary in declaration order. Lets
    /// the bUnit tests iterate without reflecting over the record's positional
    /// fields.
    /// </summary>
    /// <returns>Five tab summaries: contributors, insured-persons, decisions, dossiers, documents.</returns>
    public IReadOnlyList<ArchiveTabSummaryDto> AllTabs() =>
    [
        Contributors,
        InsuredPersons,
        Decisions,
        Dossiers,
        Documents,
    ];
}
