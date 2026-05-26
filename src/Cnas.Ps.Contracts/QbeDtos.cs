namespace Cnas.Ps.Contracts;

/// <summary>
/// R0163 / TOR UI 009 — wire DTO for the QBE filter envelope. Mirrors the server-side
/// <c>Cnas.Ps.Application.Qbe.QbeFilter</c> verbatim. The operator value rides as a stable
/// PascalCase string (see <see cref="QbeConditionDto.Operator"/>) so the contract survives
/// renumbering of the server-side enum.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> Field names, operator literals, and combinator literals are part of
/// the public API contract — renaming is a breaking change. Adding a new operator is
/// additive provided existing operators retain their string spelling.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> The QBE envelope does not carry raw database identifiers. When
/// a caller filters on a Sqid-encoded id column, the value is the Sqid string itself; the
/// server decodes it before binding to the underlying long column.
/// </para>
/// </remarks>
/// <param name="Combinator">
/// Top-level boolean operator, one of <c>"AND"</c> / <c>"OR"</c>. Case-sensitive. Default
/// (when the caller omits the field on the wire) is <c>"AND"</c>.
/// </param>
/// <param name="Conditions">Ordered list of conditions to apply.</param>
public sealed record QbeFilterDto(
    string Combinator,
    IReadOnlyList<QbeConditionDto> Conditions);

/// <summary>
/// R0163 — wire DTO for one field-condition triple. Operator is stable-string serialised
/// (PascalCase) so a wire-time JSON value never depends on the server-side enum's
/// underlying numeric.
/// </summary>
/// <param name="FieldName">
/// Canonical field name as declared in the target registry's QBE schema. Validator
/// enforces the regex <c>^[a-zA-Z][a-zA-Z0-9_]{0,63}$</c>.
/// </param>
/// <param name="Operator">
/// Operator literal — one of <c>"Equals"</c>, <c>"NotEquals"</c>, <c>"Contains"</c>,
/// <c>"StartsWith"</c>, <c>"EndsWith"</c>, <c>"GreaterThan"</c>,
/// <c>"GreaterOrEqual"</c>, <c>"LessThan"</c>, <c>"LessOrEqual"</c>,
/// <c>"Between"</c>, <c>"In"</c>, <c>"IsNull"</c>, <c>"IsNotNull"</c>. Case-sensitive.
/// </param>
/// <param name="Value">
/// Primary comparand serialised as a string; validator caps at 1024 chars. For
/// <c>"In"</c> this is a comma-separated list (max 100 entries enforced by the converter).
/// Ignored for <c>"IsNull"</c> / <c>"IsNotNull"</c>.
/// </param>
/// <param name="Value2">
/// Secondary comparand for the <c>"Between"</c> operator. Ignored otherwise.
/// </param>
public sealed record QbeConditionDto(
    string FieldName,
    string Operator,
    string? Value,
    string? Value2 = null);

/// <summary>
/// R0523 / TOR CF 03.05 — wire enum for the QBE ordering direction. Stable two-value
/// vocabulary; renaming is a breaking change. Mirrors the server-side
/// <c>Cnas.Ps.Application.Qbe.QbeSortDirection</c>.
/// </summary>
public enum QbeSortDirection
{
    /// <summary>Ascending.</summary>
    Asc = 0,

    /// <summary>Descending.</summary>
    Desc = 1,
}

/// <summary>
/// R0523 / TOR CF 03.05 — wire DTO for one entry of the multi-field ordering chain on
/// a QBE filter envelope. The position in the parent <c>Orderings</c> list maps 1:1 to
/// the position in the server-side <c>OrderBy</c>/<c>ThenBy</c> chain.
/// </summary>
/// <param name="FieldName">
/// Canonical field name as declared in the target registry's QBE schema. Validator-shared
/// regex with <see cref="QbeConditionDto.FieldName"/>: <c>^[a-zA-Z][a-zA-Z0-9_]{0,63}$</c>.
/// </param>
/// <param name="Direction">Sort direction — <see cref="QbeSortDirection.Asc"/> or <see cref="QbeSortDirection.Desc"/>.</param>
public sealed record QbeOrderingDto(
    string FieldName,
    QbeSortDirection Direction);
