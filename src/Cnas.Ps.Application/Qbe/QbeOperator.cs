namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — relational operator applied by a single <see cref="QbeCondition"/>.
/// Each operator binds the <c>Value</c> (and, for <see cref="Between"/>, <c>Value2</c>) of
/// the condition against a queryable field declared on a <see cref="QbeRegistrySchema"/>.
/// </summary>
/// <remarks>
/// <para>
/// The enum is the canonical server-side representation; on the wire the operator is
/// serialised as a stable PascalCase string (see <see cref="Cnas.Ps.Contracts.QbeConditionDto.Operator"/>).
/// Renaming a value is a breaking change to the public API.
/// </para>
/// <para>
/// <b>Type compatibility.</b> Not every operator is valid on every field type — the
/// <see cref="IQbeToLinqConverter"/> rejects e.g. <see cref="Between"/> on a <c>bool</c> field
/// with <c>QBE_OPERATOR_NOT_SUPPORTED</c>. The allowed combinations are documented in the
/// converter, not on the enum, so adding a new field type does not silently widen the
/// surface.
/// </para>
/// </remarks>
public enum QbeOperator
{
    /// <summary>Exact match on the field's declared type (default for primitives).</summary>
    Equals = 0,

    /// <summary>Inverse of <see cref="Equals"/>.</summary>
    NotEquals = 1,

    /// <summary>Substring match for string fields. Supports the <c>*</c> wildcard via R0164.</summary>
    Contains = 2,

    /// <summary>Prefix match for string fields.</summary>
    StartsWith = 3,

    /// <summary>Suffix match for string fields.</summary>
    EndsWith = 4,

    /// <summary>Strict <c>&gt;</c> on numeric / date fields.</summary>
    GreaterThan = 5,

    /// <summary>Non-strict <c>&gt;=</c> on numeric / date fields.</summary>
    GreaterOrEqual = 6,

    /// <summary>Strict <c>&lt;</c> on numeric / date fields.</summary>
    LessThan = 7,

    /// <summary>Non-strict <c>&lt;=</c> on numeric / date fields.</summary>
    LessOrEqual = 8,

    /// <summary>Inclusive range match — requires both <c>Value</c> and <c>Value2</c>.</summary>
    Between = 9,

    /// <summary>Membership match — value is a comma-separated list (max 100 entries).</summary>
    In = 10,

    /// <summary>Field is <see langword="null"/>; value bindings are ignored.</summary>
    IsNull = 11,

    /// <summary>Field is non-<see langword="null"/>; value bindings are ignored.</summary>
    IsNotNull = 12,
}
