using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Architecture rules that lock down the shape of the domain model in
/// <see cref="Cnas.Ps.Core.Domain"/>. Per CLAUDE.md §1.1 (Day-1 Foundation)
/// and the cross-cutting Soft-Delete / Audit-Trail principles, every concrete
/// domain entity must derive from <see cref="AuditableEntity"/> so that
/// CreatedAt/UpdatedAt/IsActive metadata is uniform, and must be <c>sealed</c>
/// to keep the inheritance graph flat and predictable.
/// </summary>
/// <remarks>
/// We use raw reflection here instead of NetArchTest because NetArchTest's
/// <c>AreClasses()</c> filter is built on Mono.Cecil and reports enums as classes,
/// which would force us to enumerate every enum by name. Reflection lets us use
/// <see cref="Type.IsEnum"/> for a clean, future-proof filter.
/// </remarks>
public class DomainEntityRulesTests
{
    /// <summary>The Core assembly containing every domain entity.</summary>
    private static readonly System.Reflection.Assembly CoreAssembly = typeof(AuditableEntity).Assembly;

    [Fact]
    public void Entities_DeriveFromAuditableEntity_OrAreValueObjects()
    {
        // ARRANGE — pick every concrete reference type in Cnas.Ps.Core.Domain that isn't an enum,
        // a record struct, the AuditableEntity base, or anything in a ValueObjects sub-namespace.
        var offenders = EnumerateConcreteDomainEntities()
            .Where(t => !typeof(AuditableEntity).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "every concrete entity in Cnas.Ps.Core.Domain MUST derive from AuditableEntity " +
            "to inherit Id/CreatedAt/UpdatedAt/IsActive/Xmin (CLAUDE.md §1.1 + Soft-Delete principle).");
    }

    [Fact]
    public void Entities_Are_Sealed()
    {
        // ARRANGE — domain entities must be sealed; inheritance is reserved for AuditableEntity (abstract).
        var offenders = EnumerateConcreteDomainEntities()
            .Where(t => !t.IsSealed)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "domain entities must be sealed — inheritance is reserved for AuditableEntity.");
    }

    /// <summary>
    /// Enumerates every public, concrete, non-enum reference type that lives directly inside
    /// <c>Cnas.Ps.Core.Domain</c> (excluding the <c>ValueObjects</c> sub-namespace and the
    /// abstract <see cref="AuditableEntity"/> base).
    /// </summary>
    private static IEnumerable<Type> EnumerateConcreteDomainEntities()
    {
        foreach (var type in CoreAssembly.GetTypes())
        {
            if (!type.IsPublic)
            {
                continue;
            }
            if (type.IsEnum || type.IsValueType || type.IsInterface)
            {
                continue;
            }
            if (type.IsAbstract)
            {
                continue;
            }
            var ns = type.Namespace ?? string.Empty;
            if (!string.Equals(ns, "Cnas.Ps.Core.Domain", StringComparison.Ordinal))
            {
                // Skip ValueObjects sub-namespace and any other nested namespaces.
                continue;
            }

            yield return type;
        }
    }
}
