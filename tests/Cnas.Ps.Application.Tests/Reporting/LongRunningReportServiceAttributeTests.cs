using Cnas.Ps.Application.Reporting;

namespace Cnas.Ps.Application.Tests.Reporting;

/// <summary>
/// R1904 — unit tests pinning the design of
/// <see cref="LongRunningReportServiceAttribute"/>. The marker carries no
/// data; its sole job is to be discoverable by the architecture test
/// <c>LongRunningReportServicesUseReadReplica</c>, which scans every type
/// carrying the attribute and verifies its constructor does not inject the
/// writable <c>ICnasDbContext</c>. Because the architecture test trusts the
/// attribute's targeting / inheritance shape, the contract is pinned here.
/// </summary>
public class LongRunningReportServiceAttributeTests
{
    /// <summary>
    /// The attribute MUST target classes only — never methods, properties or
    /// interfaces. A misconfigured target would let a developer accidentally
    /// mark an interface or method, which the arch test would silently ignore
    /// (because it scans concrete classes) and the read-replica seam would
    /// drift unnoticed.
    /// </summary>
    [Fact]
    public void AttributeUsage_TargetsClassesOnly()
    {
        var usage = typeof(LongRunningReportServiceAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.ValidOn.Should().Be(AttributeTargets.Class,
            "the attribute marks concrete report-service implementations and nothing else (R1904).");
    }

    /// <summary>
    /// The attribute MUST set <c>Inherited=false</c> and <c>AllowMultiple=false</c>.
    /// Inheriting the marker via a base class would let a derived service silently
    /// inherit the read-replica guarantee without the developer making an explicit
    /// choice; allowing multiple instances would invite "double-marking" noise.
    /// </summary>
    [Fact]
    public void AttributeUsage_IsNotInheritedAndDisallowsMultiple()
    {
        var usage = typeof(LongRunningReportServiceAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.Inherited.Should().BeFalse(
            "every report service must explicitly declare the read-replica contract (R1904).");
        usage.AllowMultiple.Should().BeFalse(
            "the marker is a presence/absence flag — duplicating it is meaningless (R1904).");
    }

    /// <summary>
    /// The attribute MUST be sealed so a derived attribute cannot widen its
    /// contract (e.g. flip <c>Inherited</c> on).
    /// </summary>
    [Fact]
    public void Attribute_IsSealed()
    {
        typeof(LongRunningReportServiceAttribute).IsSealed.Should().BeTrue(
            "the attribute is a leaf marker — no subclassing is allowed (R1904).");
    }
}
