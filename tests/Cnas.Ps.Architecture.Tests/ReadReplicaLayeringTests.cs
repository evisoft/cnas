using System.Reflection;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// R1904 / ARH 025 — architecture tests that pin the read-replica routing
/// guarantee for long-running report services. The seam (R0026) is already in
/// place via <see cref="IReadOnlyCnasDbContext"/>; this set of tests prevents
/// silent regressions where a new report service injects the writable
/// <see cref="ICnasDbContext"/> by mistake and pushes analytical load onto
/// the primary backend again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> Every concrete type in <c>Cnas.Ps.Infrastructure</c>
/// carrying <see cref="LongRunningReportServiceAttribute"/> MUST:
/// </para>
/// <list type="number">
///   <item>have AT LEAST one constructor parameter of type
///         <see cref="IReadOnlyCnasDbContext"/>; and</item>
///   <item>have ZERO constructor parameters of type
///         <see cref="ICnasDbContext"/>.</item>
/// </list>
/// <para>
/// <b>Hybrid services.</b> Some services in the Infrastructure layer end in
/// <c>ReportService</c> but legitimately write rows (audit findings,
/// classification snapshots). They appear in <see cref="HybridReportServicesAllowlist"/>
/// — adding a new entry requires a deliberate code-review decision.
/// </para>
/// </remarks>
public class ReadReplicaLayeringTests
{
    /// <summary>
    /// RATCHET — concrete <c>*ReportService</c> classes that legitimately write
    /// rows (audit, classification, integrity) and therefore cannot carry the
    /// pure-read marker. Each entry is the full type name; the comment next to
    /// it explains why the dual-context pattern is required.
    /// </summary>
    /// <remarks>
    /// Keep this list as small as possible. New report services SHOULD be
    /// pure-read (mark with <see cref="LongRunningReportServiceAttribute"/>);
    /// hybrid services are the exception, not the rule.
    /// </remarks>
    private static readonly HashSet<string> HybridReportServicesAllowlist = new(StringComparer.Ordinal)
    {
        // Hybrid: writes audit rows via IAuditService BUT only via the read-side
        // EF context. NOTE: this service is actually pure-read at the EF Core level
        // (audit writes go via IAuditService, not via ICnasDbContext). It is left
        // here as a defensive ratchet — if a future change inlines the audit
        // write through ICnasDbContext, the marker must be removed and this
        // allowlist entry kept.
        // Updated by R1904 iter 84: now marked with [LongRunningReportService].
        // "Cnas.Ps.Infrastructure.Services.Identity.AccessRightsReportService",
    };

    /// <summary>
    /// R1904 — every type carrying <see cref="LongRunningReportServiceAttribute"/>
    /// MUST inject <see cref="IReadOnlyCnasDbContext"/> and MUST NOT inject
    /// <see cref="ICnasDbContext"/>. This is the structural rule the marker
    /// promises to enforce.
    /// </summary>
    [Fact]
    public void LongRunningReportServices_DoNotInjectWritableContext()
    {
        var infrastructure = typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly;
        var marked = SafeGetTypes(infrastructure)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetCustomAttribute<LongRunningReportServiceAttribute>(inherit: false) is not null)
            .ToList();

        marked.Should().NotBeEmpty(
            "at least one service must carry [LongRunningReportService] — otherwise the marker is dead code (R1904).");

        var offenders = new List<string>();
        foreach (var type in marked)
        {
            // Each marked type must declare at least one constructor that takes
            // IReadOnlyCnasDbContext, and NO constructor parameter may be
            // ICnasDbContext (writable). We check every public + non-public ctor
            // because DI containers can resolve internal ctors too.
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var allParams = ctors.SelectMany(c => c.GetParameters()).ToList();

            // Rule 1: must NOT inject the writable context anywhere.
            foreach (var p in allParams)
            {
                if (p.ParameterType == typeof(ICnasDbContext))
                {
                    offenders.Add($"{type.FullName} — ctor parameter '{p.Name}' is ICnasDbContext (must be IReadOnlyCnasDbContext)");
                }
            }

            // Rule 2: must inject the read-only context in at least one ctor.
            var anyCtorTakesReadOnly = ctors.Any(c =>
                c.GetParameters().Any(p => p.ParameterType == typeof(IReadOnlyCnasDbContext)));
            if (!anyCtorTakesReadOnly)
            {
                offenders.Add($"{type.FullName} — no constructor accepts IReadOnlyCnasDbContext");
            }
        }

        offenders.Should().BeEmpty(
            "every type marked [LongRunningReportService] must inject IReadOnlyCnasDbContext " +
            "and must NOT inject the writable ICnasDbContext (R1904 / ARH 025). " +
            "Offenders:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// R1904 — pinning test that prevents accidental removal of the marker on
    /// <c>ReportingService</c>. <c>ReportingService</c> is the canonical
    /// long-running report service today; if it loses the marker, the entire
    /// guarantee evaporates silently.
    /// </summary>
    [Fact]
    public void ReportingService_IsMarkedAsLongRunningReportService()
    {
        var infrastructure = typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly;
        var reportingService = SafeGetTypes(infrastructure)
            .SingleOrDefault(t => t.FullName == "Cnas.Ps.Infrastructure.Services.ReportingService");

        reportingService.Should().NotBeNull(
            "ReportingService must exist in Cnas.Ps.Infrastructure.Services (R1904 pinning).");
        reportingService!.GetCustomAttribute<LongRunningReportServiceAttribute>(inherit: false)
            .Should().NotBeNull(
                "ReportingService MUST carry [LongRunningReportService] — the read-replica " +
                "contract for Annex 6 / 6b / ... / 6j aggregations depends on it (R1904).");
    }

    /// <summary>
    /// R1904 — pins the design of <see cref="LongRunningReportServiceAttribute"/>
    /// at the arch-test layer too. Even though the Application-layer test
    /// covers the same shape, having the structural contract checked at the
    /// arch boundary makes the intent explicit when scanning the architecture
    /// suite alone.
    /// </summary>
    [Fact]
    public void LongRunningReportServiceMarker_AppliesToClassesOnly()
    {
        var usage = typeof(LongRunningReportServiceAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.ValidOn.Should().Be(AttributeTargets.Class,
            "the marker must apply to classes only — methods or interfaces are NOT report services (R1904).");
        usage.Inherited.Should().BeFalse(
            "inheritance would let a subclass silently inherit the read-replica contract (R1904).");
        usage.AllowMultiple.Should().BeFalse(
            "the marker is a presence/absence flag (R1904).");
    }

    /// <summary>
    /// R1904 ratchet — every concrete class whose name ends in
    /// <c>ReportService</c> living in <c>Cnas.Ps.Infrastructure</c> MUST
    /// either carry the marker OR appear in
    /// <see cref="HybridReportServicesAllowlist"/>. Adding a new report
    /// service therefore forces a deliberate choice: pure-read (mark it)
    /// or hybrid (allowlist with justification).
    /// </summary>
    [Fact]
    public void AnyTypeNamedReportService_CarriesMarkerOrIsAllowlisted()
    {
        var infrastructure = typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly;

        var candidates = SafeGetTypes(infrastructure)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })
            .Where(t => t.Name.EndsWith("ReportService", StringComparison.Ordinal))
            .ToList();

        var offenders = new List<string>();
        foreach (var type in candidates)
        {
            var hasMarker = type.GetCustomAttribute<LongRunningReportServiceAttribute>(inherit: false) is not null;
            var isAllowlisted = type.FullName is not null
                && HybridReportServicesAllowlist.Contains(type.FullName);

            if (!hasMarker && !isAllowlisted)
            {
                offenders.Add(type.FullName ?? type.Name);
            }
        }

        offenders.Should().BeEmpty(
            "every *ReportService in Cnas.Ps.Infrastructure must either carry " +
            "[LongRunningReportService] OR be added to HybridReportServicesAllowlist with a justification " +
            "comment (R1904). New report services SHOULD default to pure-read (marked). " +
            "Offenders:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Reflection helper that tolerates missing-dependency exceptions surfaced by
    /// <see cref="Assembly.GetTypes"/> on assemblies that lazy-load satellite references.
    /// </summary>
    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }
}
