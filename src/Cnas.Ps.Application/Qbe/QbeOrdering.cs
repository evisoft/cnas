namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0523 / TOR CF 03.05 — direction half of a <see cref="QbeOrdering"/>. Mirrors the
/// public-facing <c>Cnas.Ps.Contracts.QbeSortDirection</c> enum on the wire; using a
/// dedicated server-side enum keeps the converter independent of the contract project
/// while preserving the stable two-value vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> The two values are part of the public API contract — renaming or
/// re-numbering is a breaking change. Adding new sort directions (e.g. a hypothetical
/// "natural" lexical sort) is intentionally NOT planned; consumers needing that
/// behaviour should pre-project the column.
/// </para>
/// </remarks>
public enum QbeSortDirection
{
    /// <summary>Ascending — <c>OrderBy</c> / <c>ThenBy</c>.</summary>
    Asc = 0,

    /// <summary>Descending — <c>OrderByDescending</c> / <c>ThenByDescending</c>.</summary>
    Desc = 1,
}

/// <summary>
/// R0523 / TOR CF 03.05 — one entry in the multi-field ordering chain attached to a
/// <see cref="QbeFilter"/>. Names the field to sort on plus the sort direction; the
/// converter resolves the field against the registry schema and emits an
/// <c>OrderBy</c>/<c>ThenBy</c> expression chain that EF Core translates to SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Field allow-list.</b> <see cref="FieldName"/> MUST be a member of the registry's
/// QBE schema; the converter rejects unknown names with
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.QbeFieldNotQueryable"/>. This is a security
/// boundary identical to the one on <see cref="QbeCondition"/> — an unrestricted
/// ordering surface would let callers force a sort on un-indexed columns (DoS vector).
/// </para>
/// <para>
/// <b>Ordering is preserved.</b> The position of each entry in the
/// <see cref="QbeFilter.Orderings"/> list maps 1:1 to the position in the
/// <c>OrderBy/ThenBy</c> chain — so a caller asking for <c>[Name ASC, CreatedAt DESC]</c>
/// gets <c>OrderBy(Name).ThenByDescending(CreatedAt)</c>. The maximum chain length is
/// not capped at this layer; callers wanting a cap should validate at the controller.
/// </para>
/// </remarks>
/// <param name="FieldName">
/// Canonical field name as declared on the registry's <see cref="QbeRegistrySchema"/>.
/// Matched ordinal, case-sensitive.
/// </param>
/// <param name="Direction">Sort direction — <see cref="QbeSortDirection.Asc"/> or <see cref="QbeSortDirection.Desc"/>.</param>
public sealed record QbeOrdering(string FieldName, QbeSortDirection Direction);
