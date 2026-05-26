using System.Reflection;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Enforces CLAUDE.md RULE 3 ("Sqids for All External IDs") at the contract boundary:
/// every <c>Id</c> / <c>*Id</c> property exposed on DTOs in <see cref="Cnas.Ps.Contracts"/>
/// must be a <see cref="string"/> (Sqid-encoded), never a raw <see cref="long"/> or
/// <see cref="int"/>. Enum-typed identifier-suffixed properties (e.g. discriminator
/// codes) are intentionally exempted because they are not database keys.
/// </summary>
public class ContractRulesTests
{
    /// <summary>The Contracts assembly containing every input/output DTO that crosses the API boundary.</summary>
    private static readonly Assembly ContractsAssembly = typeof(Cnas.Ps.Contracts.PageRequest).Assembly;

    [Fact]
    public void Output_Dtos_Have_String_Id_Not_Long()
    {
        // Output DTOs end with `Output` or `Item` by convention (e.g. ApplicationOutput, TaskInboxItem).
        AssertIdsAreStrings(typeName =>
            typeName.EndsWith("Output", StringComparison.Ordinal) ||
            typeName.EndsWith("Item", StringComparison.Ordinal),
            kind: "output");
    }

    [Fact]
    public void Input_Dtos_Have_String_Id_Not_Long()
    {
        AssertIdsAreStrings(typeName =>
            typeName.EndsWith("Input", StringComparison.Ordinal),
            kind: "input");
    }

    /// <summary>
    /// Walks every public record/class in the Contracts assembly that matches the supplied
    /// naming predicate and asserts that any <c>Id</c> or <c>*Id</c> property is a string.
    /// </summary>
    /// <param name="namePredicate">Filter selecting which DTOs to validate.</param>
    /// <param name="kind">Human-readable label ("input" or "output") used in failure messages.</param>
    private static void AssertIdsAreStrings(Func<string, bool> namePredicate, string kind)
    {
        var violations = new List<string>();

        foreach (var type in ContractsAssembly.GetTypes())
        {
            if (!type.IsPublic || !type.IsClass || type.IsAbstract)
            {
                continue;
            }
            if (!namePredicate(type.Name))
            {
                continue;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsIdLikeName(prop.Name))
                {
                    continue;
                }
                var propType = prop.PropertyType;
                var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

                // Enum-typed Id properties are categorical codes, not database keys — skip them.
                if (underlying.IsEnum)
                {
                    continue;
                }

                if (underlying != typeof(string))
                {
                    violations.Add(
                        $"{type.FullName}.{prop.Name} : {FriendlyName(propType)} — expected string (Sqid)");
                }
            }
        }

        violations.Should().BeEmpty(
            "every Id / *Id property on a Contracts {0} DTO must be a Sqid-encoded string " +
            "(CLAUDE.md RULE 3). Offenders are listed above.",
            kind);
    }

    /// <summary>True when the property name is exactly <c>Id</c> or ends with <c>Id</c> (e.g. <c>UserId</c>).</summary>
    private static bool IsIdLikeName(string name)
    {
        if (string.Equals(name, "Id", StringComparison.Ordinal))
        {
            return true;
        }
        // Reject false positives like "Valid", "Paid", "Said" — require an uppercase letter
        // before the trailing "Id" segment.
        return name.Length > 2
               && name.EndsWith("Id", StringComparison.Ordinal)
               && char.IsUpper(name[^3]);
    }

    /// <summary>Renders a CLR type into a developer-friendly notation (handles Nullable&lt;T&gt;).</summary>
    private static string FriendlyName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        return underlying is not null ? underlying.Name + "?" : type.Name;
    }
}
