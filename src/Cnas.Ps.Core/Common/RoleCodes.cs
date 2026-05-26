namespace Cnas.Ps.Core.Common;

/// <summary>
/// R0122 — stable role codes referenced by workflow performer assignments,
/// controller authorization policies, and ACL services. Centralising the literal
/// strings prevents typo-driven drift between the wire contract (mapped from MPass
/// service claims in <c>AuthenticationComposition</c>), the authorization policies
/// in <c>AuthorizationComposition</c>, and workflow-step performer codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability contract.</b> Each value is part of the public contract — renaming
/// is a breaking change to MPass claim mappings, JWT role claims, and every workflow
/// definition whose performer references the code. Add new roles by appending; never
/// reuse a retired value.
/// </para>
/// <para>
/// <b>Naming convention.</b> <c>kebab-case</c> with the <c>cnas-</c> prefix, matching
/// the existing mappings in <c>AuthenticationComposition</c>.
/// </para>
/// </remarks>
public static class RoleCodes
{
    /// <summary>The baseline authenticated CNAS user (citizen or staff with no elevated rights).</summary>
    public const string User = "cnas-user";

    /// <summary>The pension / benefit decider role — may approve / reject decisions per UC.</summary>
    public const string Decider = "cnas-decider";

    /// <summary>The CNAS administrator role — can configure non-technical operational data.</summary>
    public const string Admin = "cnas-admin";

    /// <summary>The technical administrator / super-admin role — bypasses ACLs and rule packs.</summary>
    public const string TechAdmin = "cnas-tech-admin";

    /// <summary>
    /// All known role codes, in declaration order. Used by validators that want to
    /// gate a free-text role code against the known set without hand-maintaining a
    /// parallel list.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> All =
        new System.Collections.Generic.HashSet<string>(
            new[] { User, Decider, Admin, TechAdmin },
            System.StringComparer.Ordinal);
}
