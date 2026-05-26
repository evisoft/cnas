namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1904 / ARH 025 — marks a class as a long-running report service whose
/// data access MUST run on the Postgres read-replica via the
/// <c>IReadOnlyCnasDbContext</c> seam shipped in iteration territory R0026.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the marker exists.</b> Reporting and Annex 6 / 6b / ... / 6j
/// aggregations sweep significant slices of the database and would crush the
/// primary backend if they ran there. R0026 split the EF Core surface into a
/// writable <c>ICnasDbContext</c> and a read-only <c>IReadOnlyCnasDbContext</c>
/// (the latter routed to the streaming-replication follower in production).
/// This attribute pins, at the class level, which services participate in that
/// guarantee.
/// </para>
/// <para>
/// <b>How it is enforced.</b> The architecture test
/// <c>LongRunningReportServicesUseReadReplica</c> (in
/// <c>Cnas.Ps.Architecture.Tests.ReadReplicaLayeringTests</c>) scans every
/// concrete type in <c>Cnas.Ps.Infrastructure</c> carrying this attribute and
/// asserts that:
/// </para>
/// <list type="number">
///   <item>AT LEAST one constructor parameter is <c>IReadOnlyCnasDbContext</c>,
///         and</item>
///   <item>NO constructor parameter is the writable <c>ICnasDbContext</c>.</item>
/// </list>
/// <para>
/// A separate ratchet test additionally asserts that every concrete
/// <c>*ReportService</c> class in the Infrastructure layer either carries this
/// marker or appears in an explicit allowlist with a justification comment.
/// New report services therefore force a deliberate decision: pure-read
/// (mark it) or hybrid read-and-write (allowlist with rationale).
/// </para>
/// <para>
/// <b>What it is NOT.</b> The marker carries no runtime behaviour — there is
/// no interceptor, no DI hook, no method-level effect. It is a documented
/// promise enforced at build time by the architecture suite. Removing the
/// attribute from a marked service WILL fail the
/// <c>ReportingService_IsMarkedAsLongRunningReportService</c> pinning test
/// for canonical services, and the ratchet test for any other service.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class LongRunningReportServiceAttribute : Attribute
{
}
