using System.Reflection;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Architecture rule that pairs CLAUDE.md RULE 3 ("Sqids for All External IDs") with the
/// <see cref="IExternalId"/> marker contract: every domain entity whose primary key is
/// exposed across the API boundary as a Sqid-encoded string on a Contracts DTO MUST
/// declare its intent by implementing <see cref="IExternalId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The test is a heuristic — it enumerates every public DTO in <c>Cnas.Ps.Contracts</c>
/// that carries a <c>string Id</c> property and tries to map it back to a domain entity
/// by stripping the common DTO suffixes (<c>Output</c>, <c>Input</c>, <c>Item</c>,
/// <c>Card</c>, <c>Row</c>, <c>Snapshot</c>, <c>Request</c>, <c>Response</c>,
/// <c>Summary</c>, <c>Detail</c>, <c>Dto</c>) from the DTO type name. When a matching
/// entity exists, it must implement <see cref="IExternalId"/>; when no entity matches
/// (computed / projection DTOs such as <see cref="Cnas.Ps.Contracts.SearchRow"/> or
/// <see cref="Cnas.Ps.Contracts.PublicContentCard"/>), the row is silently skipped.
/// </para>
/// <para>
/// We do NOT walk Sqid-encoded <c>*Id</c> properties beyond the primary <c>Id</c>: those
/// foreign-key fields point at entities whose own primary <c>Id</c> is already covered by
/// some other DTO (e.g. <c>TaskInboxItem.DossierId</c> is enforced via the existence of
/// <c>Dossier</c>'s primary-id surface elsewhere). The simpler rule keeps the test from
/// over-constraining DTOs that project FKs to unrelated entities.
/// </para>
/// </remarks>
public class ExternalIdContractTests
{
    /// <summary>The Contracts assembly containing every input/output DTO that crosses the API boundary.</summary>
    private static readonly Assembly ContractsAssembly = typeof(Cnas.Ps.Contracts.PageRequest).Assembly;

    /// <summary>The Core assembly containing every domain entity.</summary>
    private static readonly Assembly CoreAssembly = typeof(IExternalId).Assembly;

    /// <summary>
    /// Suffix strings stripped from a DTO type name to derive its (likely) underlying
    /// entity name. Ordered from longest to shortest so that <c>ListItem</c> trims before
    /// <c>Item</c>, etc.
    /// </summary>
    private static readonly string[] DtoSuffixes =
    [
        "Snapshot",
        "Response",
        "Request",
        "Summary",
        "Detail",
        "Output",
        "Input",
        "Card",
        "Item",
        "Row",
        "Dto",
    ];

    /// <summary>
    /// Some Contracts DTOs map to entities whose name does not share the DTO stem (e.g.
    /// <c>ApplicationOutput</c> -> <see cref="ServiceApplication"/>, <c>UserListItem</c> ->
    /// <see cref="UserProfile"/>). When the stem-stripped DTO name lives here, we redirect
    /// the entity lookup to the listed alias before resolving against the domain assembly.
    /// </summary>
    private static readonly Dictionary<string, string> DtoStemAliases =
        new(StringComparer.Ordinal)
        {
            ["Application"] = nameof(ServiceApplication),
            ["ApplicationList"] = nameof(ServiceApplication),
            ["TaskInbox"] = nameof(WorkflowTask),
            ["UserList"] = nameof(UserProfile),
            ["Profile"] = nameof(UserProfile),
            ["ServicePassportList"] = nameof(ServicePassport),
            ["ServicePassportDetail"] = nameof(ServicePassport),
            ["ContributorList"] = nameof(Contributor),
            ["InsuredPersonList"] = nameof(InsuredPerson),
            ["FailedJob"] = nameof(FailedJob),
            ["Notification"] = nameof(Notification),
        };

    [Fact]
    public void DtosWithStringId_MapToEntitiesImplementingIExternalId()
    {
        // ARRANGE — build a lookup of public, concrete entity types in Cnas.Ps.Core.Domain
        // keyed by simple name (e.g. "Dossier" -> typeof(Dossier)).
        var domainEntities = CoreAssembly.GetTypes()
            .Where(t => t.IsPublic && t.IsClass && !t.IsAbstract)
            .Where(t => string.Equals(t.Namespace, "Cnas.Ps.Core.Domain", StringComparison.Ordinal))
            .ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);

        // ARRANGE — enumerate every Contracts DTO that carries a `string Id` property.
        var dtosWithStringId = ContractsAssembly.GetTypes()
            .Where(t => t.IsPublic && (t.IsClass || t.IsValueType) && !t.IsAbstract)
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p =>
                    string.Equals(p.Name, "Id", StringComparison.Ordinal) &&
                    p.PropertyType == typeof(string)))
            .ToList();

        // ACT — for each DTO try to resolve a matching entity; failure to find one means
        // the DTO is a computed / projection (skip), success means the entity MUST mark
        // its boundary-crossing intent via IExternalId.
        var violations = new List<string>();
        foreach (var dto in dtosWithStringId)
        {
            if (!TryResolveEntity(dto.Name, domainEntities, out var entity))
            {
                // No matching entity — DTO is a computed/projection (e.g. SearchRow,
                // PublicContentCard, KpiWidget). Not a contract violation.
                continue;
            }
            if (!typeof(IExternalId).IsAssignableFrom(entity))
            {
                violations.Add(
                    $"{entity!.FullName} must implement IExternalId because " +
                    $"{dto.FullName}.Id is a Sqid-encoded string that crosses the boundary.");
            }
        }

        violations.Should().BeEmpty(
            "every domain entity whose primary key is exposed as a Sqid-encoded string on a " +
            "Contracts DTO MUST declare its boundary-crossing intent by implementing IExternalId " +
            "(CLAUDE.md RULE 3 / ARH 027). Offenders are listed above.");
    }

    /// <summary>
    /// Resolves a Contracts DTO type name to its underlying domain entity by iteratively
    /// stripping the known DTO suffixes (longest first) and consulting the
    /// <see cref="DtoStemAliases"/> redirect map at every step. Stops at the first match.
    /// </summary>
    /// <param name="dtoName">Simple type name of the DTO (e.g. <c>ApplicationListItemOutput</c>).</param>
    /// <param name="domainEntities">Lookup of public concrete entity types by simple name.</param>
    /// <param name="entity">The resolved entity type when the method returns <c>true</c>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when an entity match was found; <c>false</c> for computed / projection DTOs.</returns>
    private static bool TryResolveEntity(
        string dtoName,
        IReadOnlyDictionary<string, Type> domainEntities,
        out Type? entity)
    {
        // Try the raw DTO name first (some DTOs already match an entity, e.g. "Notification" alias).
        if (TryLookup(dtoName, domainEntities, out entity))
        {
            return true;
        }

        // Iteratively strip one suffix at a time. At each step, try the alias map and then
        // a direct entity lookup. Bound the loop to a few iterations so a pathological DTO
        // name cannot spin indefinitely.
        var current = dtoName;
        for (var i = 0; i < 5; i++)
        {
            var trimmed = StripOneSuffix(current);
            if (trimmed is null)
            {
                break;
            }
            current = trimmed;
            if (TryLookup(current, domainEntities, out entity))
            {
                return true;
            }
        }

        entity = null;
        return false;
    }

    /// <summary>
    /// Consults <see cref="DtoStemAliases"/> for a redirect and then attempts a direct
    /// dictionary lookup against the domain-entity map. The alias map wins when present
    /// because it documents known DTO-to-entity renamings (e.g. <c>Application</c> ->
    /// <see cref="ServiceApplication"/>).
    /// </summary>
    /// <param name="candidate">The candidate entity name to look up (after suffix stripping).</param>
    /// <param name="domainEntities">Lookup of public concrete entity types by simple name.</param>
    /// <param name="entity">The resolved entity type when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when the candidate (or its alias) maps to an entity.</returns>
    private static bool TryLookup(
        string candidate,
        IReadOnlyDictionary<string, Type> domainEntities,
        out Type? entity)
    {
        if (DtoStemAliases.TryGetValue(candidate, out var alias))
        {
            if (domainEntities.TryGetValue(alias, out var aliased))
            {
                entity = aliased;
                return true;
            }
        }
        if (domainEntities.TryGetValue(candidate, out var direct))
        {
            entity = direct;
            return true;
        }
        entity = null;
        return false;
    }

    /// <summary>
    /// Strips a single matching suffix from <paramref name="typeName"/> (longest match wins).
    /// Returns <c>null</c> when no known suffix applies so callers can break out of the loop.
    /// </summary>
    /// <param name="typeName">Current candidate name.</param>
    /// <returns>The trimmed name, or <c>null</c> when no suffix matches.</returns>
    private static string? StripOneSuffix(string typeName)
    {
        foreach (var suffix in DtoSuffixes)
        {
            if (typeName.Length > suffix.Length &&
                typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return typeName[..^suffix.Length];
            }
        }
        return null;
    }
}
