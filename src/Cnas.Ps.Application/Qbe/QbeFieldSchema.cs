namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — one queryable field declared on a <see cref="QbeRegistrySchema"/>.
/// The schema is the allow-list the converter consults before binding any condition: a
/// field name that is not in the schema is silently rejected, so adding a new filterable
/// column requires an explicit code change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a schema is required.</b> An unrestricted QBE primitive that could reflect over
/// arbitrary entity properties would let a hostile caller probe encrypted shadow columns,
/// audit fields, or columns that intentionally have no index (and would therefore become
/// a DoS vector). The schema makes the surface explicit and reviewable.
/// </para>
/// <para>
/// <b>Supported field types.</b> The converter supports:
/// <see cref="string"/>, <see cref="bool"/>,
/// <see cref="byte"/>/<see cref="short"/>/<see cref="int"/>/<see cref="long"/> (signed/unsigned),
/// <see cref="float"/>/<see cref="double"/>/<see cref="decimal"/>,
/// <see cref="DateTime"/>, <see cref="DateOnly"/>, and any <see cref="Enum"/>.
/// Enums are matched by name (case-insensitive) so the wire contract is stable even when
/// the enum's underlying numeric value changes.
/// </para>
/// <para>
/// <b>Case sensitivity.</b> Defaults to <see langword="false"/>; string fields are folded
/// to <see cref="StringComparison.OrdinalIgnoreCase"/> for equality and use
/// <c>EF.Functions.ILike</c> for wildcard operators. Set to <see langword="true"/> only for
/// fields where exact byte-for-byte equality is required (e.g. base64-encoded HMAC hashes
/// where case carries information).
/// </para>
/// </remarks>
/// <param name="FieldName">
/// Canonical field name. Matched against <see cref="QbeCondition.FieldName"/> ordinal,
/// case-sensitive. Must obey the validator regex
/// <c>^[a-zA-Z][a-zA-Z0-9_]{0,63}$</c> so it can flow into the LINQ expression without
/// further sanitisation.
/// </param>
/// <param name="FieldType">
/// CLR type of the entity property the field name resolves to. The converter parses the
/// condition's string value(s) according to this type.
/// </param>
/// <param name="IsCaseSensitive">
/// <see langword="true"/> when string equality must be byte-for-byte; otherwise
/// <see langword="false"/> (the default, which is the citizen-facing convention).
/// </param>
public sealed record QbeFieldSchema(
    string FieldName,
    Type FieldType,
    bool IsCaseSensitive = false);
