using System.Collections.Frozen;

namespace Cnas.Ps.Application.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — canonical allow-list of registry codes that the
/// query-budget service evaluates. Centralised next to <c>BulkRegistries</c> (R0166) so a
/// single edit registers a new registry against both subsystems.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> Registry codes are part of the public API contract — the UI maps
/// them to localised registry display names and the audit log records them verbatim.
/// Treat the constants as additive.
/// </para>
/// <para>
/// <b>Independence from <c>BulkRegistries</c>.</b> The query-budget allow-list is
/// intentionally a separate constant set even though it overlaps today with bulk:
/// registries can have a budget without supporting bulk actions (e.g. <c>AuditLog</c>),
/// and vice versa. Sharing the constant would over-couple the two subsystems.
/// </para>
/// <para>
/// <b>Case-sensitive.</b> Lookups go through <see cref="StringComparer.Ordinal"/>; the
/// PascalCase convention mirrors <c>BulkRegistries</c>.
/// </para>
/// </remarks>
public static class QueryBudgetRegistries
{
    /// <summary>Solicitanți — applicant registry (TOR §2.3 #2).</summary>
    public const string Solicitant = "Solicitant";

    /// <summary>Cereri — applications registry (TOR §2.3 #6).</summary>
    public const string Cerere = "Cerere";

    /// <summary>Sarcini — workflow-task registry (TOR §2.3 #9).</summary>
    public const string WorkflowTask = "WorkflowTask";

    /// <summary>Decizii — decisions registry (TOR §2.3 #7).</summary>
    public const string Decision = "Decision";

    /// <summary>Documente — document registry (TOR §2.3 #8).</summary>
    public const string Document = "Document";

    /// <summary>Audit log — sensitive-event journal (TOR §2.3 #14 / UC23).</summary>
    public const string AuditLog = "AuditLog";

    /// <summary>
    /// R0504 / TOR CF 01.06 — public services-catalog. Anonymous-accessible browse
    /// of <see cref="Cnas.Ps.Core.Domain.ServicePassport"/> rows; a tighter budget
    /// (1000 rows) protects the unauthenticated surface from scraper-style
    /// enumeration that would otherwise materialise the entire catalogue in one
    /// call.
    /// </summary>
    public const string PublicCatalog = "PublicCatalog";

    /// <summary>
    /// R0822 / TOR Annex 8 (BP 1.2-M) — contribution-declarations registry
    /// explorer. The Declarations table accumulates rapidly (one row per payer
    /// per month per kind), so the budget gate forces operators to narrow by
    /// payer + kind + reporting-window before materialising — mirroring the
    /// Annex 1 §8.1.3 search-criteria contract.
    /// </summary>
    public const string Declaration = "Declaration";

    /// <summary>
    /// R1600 / TOR Annex 3.8 — executory documents registry explorer. Documents
    /// accumulate at a moderate rate (one row per court-issued instrument per
    /// debtor) so the budget gate forces operators to narrow by debtor /
    /// status / kind before materialising the full registry.
    /// </summary>
    public const string ExecutoryDocument = "ExecutoryDocument";

    /// <summary>
    /// R2270 / TOR SEC 023-024 — user-group registry explorer. Volume is low
    /// (a few hundred groups per organisation) but the same QBE plumbing is
    /// exposed so admin tooling can compose filters consistently with the
    /// other registries.
    /// </summary>
    public const string UserGroup = "UserGroup";

    /// <summary>
    /// Frozen ordinal allow-list. Frozen because the set is fixed at build time; policy
    /// resolvers can consult <see cref="FrozenSet{T}.Contains"/> as O(1) without the
    /// overhead of a per-call <see cref="HashSet{T}"/> construction.
    /// </summary>
    public static readonly FrozenSet<string> All = new[]
    {
        Solicitant,
        Cerere,
        WorkflowTask,
        Decision,
        Document,
        AuditLog,
        PublicCatalog,
        Declaration,
        ExecutoryDocument,
        UserGroup,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="registry"/> is one of the canonical
    /// query-budget codes. Case-sensitive — <c>solicitant</c> is NOT valid.
    /// </summary>
    /// <param name="registry">Candidate registry code; nullable.</param>
    /// <returns><c>true</c> when the code is in the allow-list; <c>false</c> otherwise.</returns>
    public static bool IsKnown(string? registry) =>
        !string.IsNullOrWhiteSpace(registry) && All.Contains(registry);
}
