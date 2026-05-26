namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — frozen, registry-scoped allow-list of queryable fields. The
/// schema is built once at startup by an implementation of
/// <see cref="IQbeRegistrySchemaProvider"/> and consulted on every QBE call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mapping to registries.</b> The <see cref="RegistryCode"/> is the same opaque string
/// the rest of the system uses (see <see cref="Cnas.Ps.Application.QueryBudget.QueryBudgetRegistries"/>
/// and the bulk-action registry). Schemas live next to those registry concepts so a single
/// registry name composes a budget, a bulk surface, and a QBE schema.
/// </para>
/// <para>
/// <b>Field uniqueness.</b> Within one schema, <see cref="QbeFieldSchema.FieldName"/> is
/// expected to be unique (ordinal). The provider implementation does not enforce this at
/// runtime — duplicates would surface during the converter's first-match lookup.
/// </para>
/// </remarks>
/// <param name="RegistryCode">Registry code (e.g. <c>"Solicitant"</c>).</param>
/// <param name="Fields">Ordered list of queryable fields. Ordering carries no semantics.</param>
public sealed record QbeRegistrySchema(
    string RegistryCode,
    IReadOnlyList<QbeFieldSchema> Fields)
{
    /// <summary>
    /// Resolves a field-name to its schema entry. Case-sensitive (matches
    /// <see cref="QbeCondition.FieldName"/> semantics).
    /// </summary>
    /// <param name="fieldName">The candidate field name, nullable.</param>
    /// <returns>The matching <see cref="QbeFieldSchema"/> or <see langword="null"/> when absent.</returns>
    public QbeFieldSchema? FindField(string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
        {
            return null;
        }
        // Linear scan is fine — schemas typically have ≤ 20 fields and the dictionary
        // overhead would be larger than the lookup cost.
        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].FieldName, fieldName, StringComparison.Ordinal))
            {
                return Fields[i];
            }
        }
        return null;
    }
}
