using System.Collections;
using System.Reflection;
using System.Text.Json;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0183 / SEC 043 — reference reflection-based implementation of
/// <see cref="IAuditDiffComputer"/>. Pure (no DI, no I/O) so the diff is
/// deterministic and trivially unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Property scan.</b> The computer scans public readable instance properties of
/// the runtime type. Indexers and write-only properties are skipped. For each
/// property listed in <see cref="AuditFieldPolicyView.TrackedFields"/> we read
/// the value from the before snapshot, the after snapshot, and compare:
/// <list type="bullet">
///   <item>Primitives + strings: <see cref="object.Equals(object?, object?)"/>.</item>
///   <item><see cref="System.DateTime"/>: collapse to UTC before comparison so two
///         logically-equal-but-differently-kinded values do not produce a spurious
///         diff. <see cref="System.DateTime"/> with <see cref="DateTimeKind.Unspecified"/>
///         is treated as UTC.</item>
///   <item>Collections (anything implementing <see cref="IEnumerable"/> other than
///         <see cref="string"/>): JSON-serialise both sides and compare strings.
///         Cheap and sufficient for the audit purpose — accepts ordering changes as
///         "diffs", which mirrors how operators expect to see audit rows.</item>
///   <item>Other reference types: JSON-shape compare (same as collections).</item>
/// </list>
/// </para>
/// <para>
/// <b>Suppression.</b> When a tracked property is also listed in
/// <see cref="AuditFieldPolicyView.SuppressedFields"/>, the resulting
/// <see cref="AuditDiffEntry"/> records the change but the before / after values
/// are the literal JSON string <c>"\"[redacted]\""</c> — operators see WHICH field
/// changed without leaking its value.
/// </para>
/// <para>
/// <b>Null-snapshot semantics.</b> <c>before == null</c> models creation — every
/// tracked field appears with <c>BeforeJson = null</c>. <c>after == null</c> models
/// deletion — every tracked field appears with <c>AfterJson = null</c>. Both nulls
/// is invalid; we throw <see cref="ArgumentNullException"/> rather than silently
/// emit an empty diff.
/// </para>
/// </remarks>
public sealed class AuditDiffComputer : IAuditDiffComputer
{
    /// <summary>Literal JSON value emitted in place of a suppressed property's actual value.</summary>
    internal const string RedactedJson = "\"[redacted]\"";

    private readonly ISqidService _sqids;

    /// <summary>
    /// Default JSON serialisation options — matches the conventions used elsewhere
    /// in the codebase (system defaults; nulls preserved for diff fidelity).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Constructs the computer with the Sqid encoder for the diff payload's <c>EntityId</c>.</summary>
    /// <param name="sqids">Sqid service used to encode the raw long primary key.</param>
    public AuditDiffComputer(ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(sqids);
        _sqids = sqids;
    }

    /// <inheritdoc />
    public AuditDiff? Compute(string entityType, object? before, object? after, AuditFieldPolicyView policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(policy);
        if (before is null && after is null)
        {
            throw new ArgumentNullException(nameof(after), "Both before and after snapshots are null — caller must supply at least one.");
        }

        // The runtime type is taken from whichever snapshot is non-null. Both are
        // expected to be the same CLR type when both are present; if a caller hands
        // in a mismatched pair we still operate against the after-type by
        // preference (the policy view's EntityType is the canonical handle).
        var runtimeType = (after ?? before)!.GetType();

        var entries = new List<AuditDiffEntry>(policy.TrackedFields.Count);
        var anyDifferent = false;

        // Iterate the tracked fields in declared order so the persisted diff layout
        // is deterministic — operators reading the audit row see fields in the
        // same order they configured them.
        foreach (var fieldName in policy.TrackedFields)
        {
            var property = ResolveProperty(runtimeType, fieldName);
            if (property is null)
            {
                // A tracked field that does not exist on the runtime type is a
                // configuration drift. Surface it as a diff entry with both sides
                // null so operators see the misconfiguration without crashing the
                // write path.
                entries.Add(new AuditDiffEntry(fieldName, null, null));
                continue;
            }

            var beforeValue = before is null ? null : property.GetValue(before);
            var afterValue = after is null ? null : property.GetValue(after);

            var changed = !AreEqualForAudit(beforeValue, afterValue);
            if (!changed)
            {
                continue;
            }

            anyDifferent = true;

            var isSuppressed = policy.SuppressedFields.Contains(fieldName);
            var beforeJson = before is null
                ? null
                : isSuppressed ? RedactedJson : Serialize(beforeValue);
            var afterJson = after is null
                ? null
                : isSuppressed ? RedactedJson : Serialize(afterValue);

            entries.Add(new AuditDiffEntry(fieldName, beforeJson, afterJson));
        }

        if (policy.RequireAnyChange && !anyDifferent)
        {
            return null;
        }

        // Resolve the entity's external id. Prefer the after snapshot; fall back to
        // before for the deletion case. The id property is named "Id" by convention
        // across every AuditableEntity-derived type.
        var entityId = ResolveSqidEntityId(runtimeType, after ?? before!);

        return new AuditDiff(
            EntityType: entityType,
            EntityId: entityId,
            Entries: entries);
    }

    /// <summary>
    /// Resolves a property by exact name (CLR property names are case-sensitive).
    /// Skips indexers and write-only properties.
    /// </summary>
    /// <param name="type">Runtime type of the snapshot.</param>
    /// <param name="name">Property name as listed on the policy.</param>
    /// <returns>The property info, or <c>null</c> when the name is unknown.</returns>
    private static PropertyInfo? ResolveProperty(Type type, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanRead || prop.GetIndexParameters().Length > 0)
        {
            return null;
        }
        return prop;
    }

    /// <summary>
    /// Encodes the entity's <c>Id</c> property via the Sqid service per CLAUDE.md
    /// RULE 3. When the property is missing or non-numeric the empty string is
    /// returned — the diff payload still flows but the id field is empty rather
    /// than crash the write path.
    /// </summary>
    /// <param name="type">Runtime type of the snapshot.</param>
    /// <param name="instance">The snapshot whose id we encode.</param>
    /// <returns>The Sqid-encoded external id, or empty string when unresolvable.</returns>
    private string ResolveSqidEntityId(Type type, object instance)
    {
        var idProp = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProp is null)
        {
            return string.Empty;
        }
        var idValue = idProp.GetValue(instance);
        if (idValue is long l)
        {
            return _sqids.Encode(l);
        }
        if (idValue is int i)
        {
            return _sqids.Encode(i);
        }
        return string.Empty;
    }

    /// <summary>
    /// Equality semantics for the diff comparison — see class remarks for the
    /// per-shape rules.
    /// </summary>
    /// <param name="a">Before value.</param>
    /// <param name="b">After value.</param>
    /// <returns><c>true</c> when the two are equal for audit purposes.</returns>
    private static bool AreEqualForAudit(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (a is DateTime da && b is DateTime db)
        {
            return ToUtc(da) == ToUtc(db);
        }

        // Strings short-circuit ahead of the IEnumerable branch since they implement
        // IEnumerable<char>.
        if (a is string sa && b is string sb)
        {
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        // Collection-shape comparison via JSON for everything else that enumerates.
        if (a is IEnumerable && b is IEnumerable)
        {
            return string.Equals(Serialize(a), Serialize(b), StringComparison.Ordinal);
        }

        // Generic reference types — JSON-shape compare for sub-records / nested
        // value objects so structural equality wins over reference equality.
        if (!a.GetType().IsPrimitive && !a.GetType().IsEnum && !(a is decimal) && !(a is Guid))
        {
            if (a is IFormattable || a is IComparable)
            {
                // Primitive-ish: fall through to Equals.
            }
            else
            {
                return string.Equals(Serialize(a), Serialize(b), StringComparison.Ordinal);
            }
        }

        return a.Equals(b);
    }

    /// <summary>
    /// Collapses a <see cref="DateTime"/> to UTC for audit equality. Unspecified
    /// kind is treated as UTC so a manually-constructed value does not surface as
    /// a difference against the stored kind.
    /// </summary>
    /// <param name="value">Source datetime.</param>
    /// <returns>UTC-projected datetime.</returns>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Serialises a value to JSON using the default Cnas writer options. The
    /// resulting string preserves type fidelity (numbers as numbers, strings
    /// quoted, etc.) so downstream consumers can re-parse with confidence.
    /// </summary>
    /// <param name="value">Value to serialise; may be <c>null</c>.</param>
    /// <returns>The JSON text — the literal string <c>"null"</c> when the value is null.</returns>
    private static string Serialize(object? value)
        => JsonSerializer.Serialize(value, JsonOptions);
}
