using System.Collections.Frozen;

namespace Cnas.Ps.Application.BulkActions;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — canonical allow-list of registry codes that bulk
/// selections may target. Centralised so the validators (FluentValidation), the service
/// layer (filter-resolver dispatch), and the operation descriptors all reference the
/// same set; adding a new registry is a single edit here plus a new
/// <c>IBulkSelectionFilterResolver</c> implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> Registry codes are part of the public API contract — once a
/// caller has persisted a selection with <c>Registry = "Cerere"</c>, renaming the code
/// would orphan the persisted row. Treat the constants as additive.
/// </para>
/// <para>
/// <b>Case-sensitive.</b> The validator compares with <see cref="StringComparer.Ordinal"/>
/// so <c>cerere</c> is NOT a valid alternative for <c>Cerere</c>. This mirrors the
/// PascalCase convention used elsewhere (e.g. <c>SavedSearch.Registry</c>).
/// </para>
/// </remarks>
public static class BulkRegistries
{
    /// <summary>Solicitanți — applicant registry (TOR §2.3 #2).</summary>
    public const string Solicitant = "Solicitant";

    /// <summary>Cereri — applications registry (TOR §2.3 #6).</summary>
    public const string Cerere = "Cerere";

    /// <summary>Sarcini — workflow-task registry (TOR §2.3 #9).</summary>
    public const string WorkflowTask = "WorkflowTask";

    /// <summary>Decizii — decisions registry (TOR §2.3 #7).</summary>
    public const string Decision = "Decision";

    /// <summary>
    /// Plătitori — contributor registry (TOR Annex 1). Added in R0305 for the
    /// bulk branch-reassignment operation (BP 1.8). The selection-side filter
    /// resolver is intentionally deferred — the only bulk operation today
    /// (<c>Contributor.ReassignBranch</c>) targets an explicit-include id list
    /// supplied by the caller, which the runner satisfies without a resolver.
    /// </summary>
    public const string Contributor = "Contributor";

    /// <summary>
    /// Frozen ordinal allow-list. Frozen because the set is fixed at build time;
    /// validators and resolvers can consult <see cref="FrozenSet{T}.Contains"/> as
    /// an O(1) lookup without the overhead of a per-call <see cref="HashSet{T}"/>
    /// construction.
    /// </summary>
    public static readonly FrozenSet<string> All = new[]
    {
        Solicitant,
        Cerere,
        WorkflowTask,
        Decision,
        Contributor,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="registry"/> is one of the canonical
    /// codes. Case-sensitive — <c>cerere</c> is NOT valid.
    /// </summary>
    /// <param name="registry">Candidate registry code; nullable.</param>
    /// <returns><c>true</c> when the code is in the allow-list; <c>false</c> otherwise.</returns>
    public static bool IsKnown(string? registry) =>
        !string.IsNullOrWhiteSpace(registry) && All.Contains(registry);
}
