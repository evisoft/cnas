namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — one field-condition triple in a <see cref="QbeFilter"/>. Names the
/// queryable field, the relational <see cref="Operator"/>, and the comparand
/// <see cref="Value"/> (plus <see cref="Value2"/> for <see cref="QbeOperator.Between"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>String-typed values.</b> The comparand is carried as a string at the boundary and is
/// parsed against the field's declared <see cref="QbeFieldSchema.FieldType"/> by the
/// converter. Dates use ISO 8601, numbers use the invariant culture, bools use ASCII
/// <c>true</c>/<c>false</c>. The converter rejects malformed input with a stable
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.QbeValueInvalid"/> failure code so the UI
/// can surface a field-targeted prompt.
/// </para>
/// <para>
/// <b>Field allow-list.</b> <see cref="FieldName"/> must match a member of the registry
/// schema returned by <see cref="IQbeRegistrySchemaProvider"/>. Arbitrary entity properties
/// are NEVER queryable — the converter rejects unknown names with
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.QbeFieldNotQueryable"/>. This is a security
/// boundary, not a convenience: an unrestricted QBE form would let a caller probe every
/// column on an entity, including audit / encryption-shadow columns that have no business
/// being filterable at the API surface.
/// </para>
/// </remarks>
/// <param name="FieldName">
/// Canonical field name as declared on the registry schema. Matched ordinal, case-sensitive.
/// </param>
/// <param name="Operator">Relational operator applied between the field and the value(s).</param>
/// <param name="Value">
/// Primary comparand serialised as a string. Ignored for
/// <see cref="QbeOperator.IsNull"/> / <see cref="QbeOperator.IsNotNull"/>. For
/// <see cref="QbeOperator.In"/> this is a comma-separated list.
/// </param>
/// <param name="Value2">
/// Secondary comparand serialised as a string. Only consumed by
/// <see cref="QbeOperator.Between"/>; ignored otherwise.
/// </param>
public sealed record QbeCondition(
    string FieldName,
    QbeOperator Operator,
    string? Value,
    string? Value2 = null);
