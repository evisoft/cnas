using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0525 / TOR CF 03.08 — heuristic <see cref="ISearchSuggestionService"/> implementation.
/// Consults the QBE registry schema to detect whether the caller's filter omits the
/// canonical discriminator field for the registry; when row count is above the
/// configured threshold it emits a stable
/// <see cref="SearchSuggestionDto"/> targeting that field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default threshold.</b> Hard-coded at 500 rows in this batch. The TOR (CF 03.08)
/// requires the system to "suggest narrowing the criteria when the result range is too
/// wide"; 500 mirrors the magnitude of the tightest budget guard registry policy
/// (<c>QueryBudgetRegistries.Solicitant</c>) so the suggestion fires before the budget
/// guard would refuse outright. A future iteration can promote this to a per-registry
/// configurable value.
/// </para>
/// <para>
/// <b>Discriminator catalogue.</b> Per-registry canonical discriminator fields:
/// <list type="bullet">
///   <item><c>"Solicitant"</c> -> <c>IsActive</c>.</item>
///   <item><c>"Cerere"</c> / Application -> <c>Status</c>.</item>
///   <item><c>"WorkflowTask"</c> -> <c>Status</c>.</item>
///   <item><c>"Decision"</c> -> <c>IsActive</c>.</item>
/// </list>
/// Adding a registry to the catalogue is a one-line edit in <see cref="Discriminators"/>.
/// </para>
/// </remarks>
/// <param name="schemas">QBE registry schema provider — used to validate the discriminator field exists on the supplied registry.</param>
public sealed class SearchSuggestionService(IQbeRegistrySchemaProvider schemas) : ISearchSuggestionService
{
    private readonly IQbeRegistrySchemaProvider _schemas = schemas;

    /// <summary>
    /// Row-count threshold above which the heuristic emits a suggestion. Hard-coded
    /// pending a per-registry configurable equivalent.
    /// </summary>
    internal const int DefaultThreshold = 500;

    /// <summary>
    /// Canonical per-registry discriminator-field catalogue. Adding a new registry is
    /// a one-line edit here. Kept private + readonly so callers cannot mutate the
    /// catalogue at runtime.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Discriminators =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Solicitant"] = "IsActive",
            ["Cerere"] = "Status",
            ["WorkflowTask"] = "Status",
            ["Decision"] = "IsActive",
        };

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SearchSuggestionDto>>> SuggestRefinementsAsync(
        string registry,
        QbeFilter? currentFilter,
        int currentRowCount,
        CancellationToken ct)
    {
        // Below the threshold — fast path, no suggestion.
        if (currentRowCount <= DefaultThreshold)
        {
            IReadOnlyList<SearchSuggestionDto> empty = Array.Empty<SearchSuggestionDto>();
            return Task.FromResult(Result<IReadOnlyList<SearchSuggestionDto>>.Success(empty));
        }

        // Unknown registry / no discriminator catalogued — emit no suggestion. We do
        // NOT fail because the caller may legitimately query a registry the heuristic
        // does not yet cover; failing here would break unrelated list endpoints.
        if (!Discriminators.TryGetValue(registry, out var discriminatorField))
        {
            IReadOnlyList<SearchSuggestionDto> empty = Array.Empty<SearchSuggestionDto>();
            return Task.FromResult(Result<IReadOnlyList<SearchSuggestionDto>>.Success(empty));
        }

        // Defensive: the QBE schema for the registry must declare the discriminator
        // field. If the schema author dropped it, do not emit a suggestion the UI
        // cannot honour — that would surface as a confusing prompt.
        var schema = _schemas.GetForRegistry(registry);
        if (schema is null || schema.FindField(discriminatorField) is null)
        {
            IReadOnlyList<SearchSuggestionDto> empty = Array.Empty<SearchSuggestionDto>();
            return Task.FromResult(Result<IReadOnlyList<SearchSuggestionDto>>.Success(empty));
        }

        // Already filtering on the discriminator — the caller has done their part.
        if (FilterReferencesField(currentFilter, discriminatorField))
        {
            IReadOnlyList<SearchSuggestionDto> empty = Array.Empty<SearchSuggestionDto>();
            return Task.FromResult(Result<IReadOnlyList<SearchSuggestionDto>>.Success(empty));
        }

        // Emit the structured suggestion. Code intentionally encodes the FIELD
        // category ("Status") so a future variant ("AddStatusFilter") for Decision
        // can reuse the same code with a different FieldName.
        var code = discriminatorField switch
        {
            "Status" => "AddStatusFilter",
            "IsActive" => "AddStatusFilter",
            _ => "AddStatusFilter",
        };
        var example = discriminatorField switch
        {
            "IsActive" => "true",
            _ => null,
        };
        IReadOnlyList<SearchSuggestionDto> suggestions = new[]
        {
            new SearchSuggestionDto(
                Code: code,
                FieldName: discriminatorField,
                ReasonCode: "TooBroad",
                ExampleValue: example),
        };
        _ = ct; // contract is async-shaped; no I/O in this iteration.
        return Task.FromResult(Result<IReadOnlyList<SearchSuggestionDto>>.Success(suggestions));
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="filter"/> contains a
    /// condition naming <paramref name="fieldName"/> (case-sensitive, mirrors the QBE
    /// converter's name comparison).
    /// </summary>
    /// <param name="filter">Caller-supplied filter envelope; nullable.</param>
    /// <param name="fieldName">Canonical field name to look for.</param>
    /// <returns><see langword="true"/> when at least one condition targets the field.</returns>
    private static bool FilterReferencesField(QbeFilter? filter, string fieldName)
    {
        if (filter is null || filter.Conditions is null || filter.Conditions.Count == 0)
        {
            return false;
        }
        for (var i = 0; i < filter.Conditions.Count; i++)
        {
            if (string.Equals(filter.Conditions[i].FieldName, fieldName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
