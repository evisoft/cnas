using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// R0026 — read-only EF Core context routed to the Postgres streaming-replication
/// replica per TOR PSR 006 / ARH 025. Reporting aggregations (Annex 6/6b/.../6j)
/// and long-running registry listing queries (Annex 5/6) consume this context so
/// the primary Postgres backend is not crushed by analytical workloads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition strategy.</b> The context derives from <see cref="CnasDbContext"/>
/// so it reuses every <c>IEntityTypeConfiguration&lt;T&gt;</c>, the
/// <c>CnasModelCacheKeyFactory</c> wiring, and the entire <c>OnModelCreating</c>
/// chain unchanged. Three behaviours flip on top of the base:
/// </para>
/// <list type="bullet">
///   <item><see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.QueryTrackingBehavior"/>
///         defaults to <see cref="QueryTrackingBehavior.NoTracking"/> — reporting
///         queries never mutate rows, so tracking is dead weight.</item>
///   <item><see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.AutoDetectChangesEnabled"/>
///         is <c>false</c> — there is nothing to detect because <c>SaveChanges</c>
///         is blocked.</item>
///   <item>Every <c>SaveChanges</c> / <c>SaveChangesAsync</c> overload throws
///         <see cref="InvalidOperationException"/> — accidental writes through the
///         replica surface are a loud bug, not a silent commit against a stale
///         snapshot.</item>
/// </list>
/// <para>
/// <b>Connection-string routing.</b> The DI wiring binds this type's
/// <see cref="DbContextOptions{T}"/> against <c>ConnectionStrings:PostgresReadReplica</c>.
/// When the replica connection string is unset (dev, single-Postgres staging) the
/// wiring transparently falls back to the primary <c>ConnectionStrings:Postgres</c>
/// and emits a WARN log line so operators see the fallback. Replica lag is BEST-EFFORT
/// eventual consistency — services that need read-your-own-writes stay on
/// <c>ICnasDbContext</c>.
/// </para>
/// <para>
/// <b>Design-time tooling.</b> No design-time factory is registered for this type
/// because migrations are owned exclusively by <see cref="CnasDbContext"/> — the
/// replica receives the same schema via streaming replication, not via a separate
/// EF migration history.
/// </para>
/// </remarks>
public class CnasReadOnlyDbContext : CnasDbContext
{
    /// <summary>
    /// Constructs the read-only context. The constructor takes
    /// <see cref="DbContextOptions{T}"/> bound to <see cref="CnasReadOnlyDbContext"/>
    /// (so the DI container can resolve a SECOND set of options against the replica
    /// connection string without colliding with the primary
    /// <see cref="DbContextOptions{T}"/> bound to <see cref="CnasDbContext"/>) and
    /// bridges into the base by passing the options through unchanged — EF Core's
    /// <see cref="DbContext"/> constructor only inspects the option extensions,
    /// not the generic <c>TContext</c> parameter at runtime.
    /// </summary>
    /// <param name="options">
    /// EF Core options for the read-only context. Wired by DI against the replica
    /// connection string (or the primary as a fallback).
    /// </param>
    public CnasReadOnlyDbContext(DbContextOptions<CnasReadOnlyDbContext> options) : base(options)
    {
        // Reporting and listing queries never mutate rows — turning tracking off
        // by default is a small win on every aggregation. AutoDetectChanges has
        // no work to do because SaveChanges is blocked below.
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    /// <summary>
    /// Blocks synchronous writes through the replica surface. The same
    /// <see cref="InvalidOperationException"/> is thrown by every
    /// <c>SaveChanges</c> overload so a caller that bypasses the async path
    /// (legacy or tooling) still sees the same diagnostic.
    /// </summary>
    /// <returns>Never returns — always throws.</returns>
    /// <exception cref="InvalidOperationException">Always — the replica is read-only.</exception>
    public override int SaveChanges()
        => throw new InvalidOperationException(ReadOnlyMessage);

    /// <summary>
    /// Blocks synchronous writes through the replica surface (with the EF
    /// <c>acceptAllChangesOnSuccess</c> hook). See <see cref="SaveChanges()"/>.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">Ignored — the context never reaches the persistence boundary.</param>
    /// <returns>Never returns — always throws.</returns>
    /// <exception cref="InvalidOperationException">Always — the replica is read-only.</exception>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new InvalidOperationException(ReadOnlyMessage);

    /// <summary>
    /// Blocks asynchronous writes through the replica surface. Reporting and
    /// listing services consume <c>IReadOnlyCnasDbContext</c> which has no
    /// <c>SaveChangesAsync</c> method, but a misconfigured caller injecting the
    /// concrete type directly still surfaces a loud failure here.
    /// </summary>
    /// <param name="cancellationToken">Ignored — the context never reaches the persistence boundary.</param>
    /// <returns>Never returns — always throws.</returns>
    /// <exception cref="InvalidOperationException">Always — the replica is read-only.</exception>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ReadOnlyMessage);

    /// <summary>
    /// Blocks asynchronous writes through the replica surface (with the EF
    /// <c>acceptAllChangesOnSuccess</c> hook). See
    /// <see cref="SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="acceptAllChangesOnSuccess">Ignored — the context never reaches the persistence boundary.</param>
    /// <param name="cancellationToken">Ignored — the context never reaches the persistence boundary.</param>
    /// <returns>Never returns — always throws.</returns>
    /// <exception cref="InvalidOperationException">Always — the replica is read-only.</exception>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ReadOnlyMessage);

    /// <summary>
    /// Diagnostic emitted by every <c>SaveChanges</c> overload. Names the
    /// concrete context so a developer staring at the stack trace immediately
    /// knows which abstraction was the wrong one to use.
    /// </summary>
    private const string ReadOnlyMessage =
        "CnasReadOnlyDbContext is read-only — route writes through ICnasDbContext (the primary connection) instead.";
}
