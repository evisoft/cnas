using Xunit;

namespace Cnas.Ps.Infrastructure.Tests.Observability;

/// <summary>
/// xUnit collection grouping every test class that exercises a production callsite
/// emitting on the <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter"/>. The
/// meter is process-static, so two parallel test runs hitting the same counter would
/// be observed by both classes' <c>MeterListener</c>s — producing flaky cross-test
/// pollution (e.g. a second drainer flush completing concurrently would inflate the
/// "exactly 1 increment" assertion).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> is set to <c>true</c>
/// so xUnit serialises every member class. Tests inside each class still run in their
/// own xUnit lifetime, so per-test state remains isolated; only inter-class
/// concurrency is suppressed.
/// </para>
/// <para>
/// Membership is by <see cref="CollectionAttribute"/> applied to each affected test
/// class. Adding a new test that touches <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter"/>
/// MUST tag the class with <c>[Collection(CnasMeterCollection.Name)]</c> so the
/// serialisation invariant holds.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CnasMeterCollection
{
    /// <summary>Stable collection name referenced by every member class.</summary>
    public const string Name = "CnasMeter";
}
