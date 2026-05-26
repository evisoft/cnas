using Cnas.Ps.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// R2175 / R2134 / R0026 — DI extension that registers the primary and
/// read-replica DbContexts together with their aliases
/// (<see cref="ICnasDbContext"/>, <see cref="IReadOnlyCnasDbContext"/>) from
/// a single <see cref="CnasDbContextOptions"/> snapshot. This is the
/// composition seam the Application layer consumes — the OLTP / OLAP split
/// (TOR ARH 025) and the read-replica reporting routing (TOR PSR 006) flow
/// through here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated extension.</b>
/// <see cref="InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/>
/// historically wired the DbContexts inline against
/// <c>ConnectionStrings:Postgres</c> + <c>ConnectionStrings:PostgresReadReplica</c>.
/// Hoisting the wiring into <c>AddCnasDataPersistence</c> achieves three goals:
/// <list type="bullet">
///   <item>One method to call from tests that need a deterministic InMemory
///         topology — the optional <c>primaryBuilder</c> / <c>replicaBuilder</c>
///         hooks let the suite swap the provider without spinning up
///         Postgres.</item>
///   <item>Single source of truth for the fallback policy — the
///         <see cref="ReadReplicaConfiguration"/> resolver is exercised
///         identically by production and tests, so the WARN log line is
///         actually proven to fire.</item>
///   <item>Strongly-typed configuration via <see cref="CnasDbContextOptions"/>
///         instead of magic string keys.</item>
/// </list>
/// </para>
/// <para>
/// <b>Production wire-up.</b>
/// <see cref="InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/>
/// continues to register the production Npgsql topology directly because the
/// Npgsql pool sizing (<see cref="PostgresPoolOptions"/>) and the PgBouncer
/// quirks are baked into that path. This extension is the test-and-future
/// surface; a follow-up iteration may rebase <c>AddCnasInfrastructure</c> on
/// top of it once the option-bind ergonomics are validated in tests.
/// </para>
/// </remarks>
public static class DataPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CnasDbContext"/> + <see cref="CnasReadOnlyDbContext"/>
    /// (plus the <see cref="ICnasDbContext"/> / <see cref="IReadOnlyCnasDbContext"/>
    /// aliases) on <paramref name="services"/> from a strongly-typed
    /// <see cref="CnasDbContextOptions"/> snapshot. Throws when the primary
    /// connection string is empty / whitespace.
    /// </summary>
    /// <param name="services">Service collection to extend.</param>
    /// <param name="options">
    /// Strongly-typed options bound from <see cref="CnasDbContextOptions.SectionName"/>.
    /// </param>
    /// <param name="loggerFactory">
    /// Logger factory used by <see cref="ReadReplicaConfiguration"/> to emit
    /// the WARN line when the replica connection string is unset. Tests pass
    /// a capturing factory; production passes
    /// <see cref="NullLoggerFactory.Instance"/> or the host's factory.
    /// </param>
    /// <param name="primaryBuilder">
    /// Optional escape hatch for tests — when non-null, the
    /// <see cref="DbContextOptionsBuilder"/> for the primary context is
    /// configured by this delegate INSTEAD OF the default Npgsql wiring.
    /// Production must leave this <c>null</c>.
    /// </param>
    /// <param name="replicaBuilder">
    /// Optional escape hatch for tests — same contract as
    /// <paramref name="primaryBuilder"/>, for the read-only context.
    /// </param>
    /// <returns>The same service collection for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/>, <paramref name="options"/>, or
    /// <paramref name="loggerFactory"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CnasDbContextOptions.PrimaryConnectionString"/> is empty —
    /// a host with no primary endpoint cannot start, and deferring the
    /// failure to first-query-time is harder to diagnose.
    /// </exception>
    public static IServiceCollection AddCnasDataPersistence(
        this IServiceCollection services,
        CnasDbContextOptions options,
        ILoggerFactory loggerFactory,
        Action<DbContextOptionsBuilder>? primaryBuilder = null,
        Action<DbContextOptionsBuilder>? replicaBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (string.IsNullOrWhiteSpace(options.PrimaryConnectionString))
        {
            throw new InvalidOperationException(
                "CnasDbContextOptions.PrimaryConnectionString is required — the host cannot start without a primary database endpoint.");
        }

        // Resolve the replica connection string with the canonical fallback policy.
        // The resolver itself emits the WARN line when the replica is unset so the
        // diagnostic is consistent with the production path that goes through
        // ConnectionStrings:* keys in InfrastructureServiceCollectionExtensions.
        var replicaConnectionString = ResolveReplica(options, loggerFactory);

        // Primary context.
        services.AddDbContext<CnasDbContext>(opts =>
        {
            if (primaryBuilder is not null)
            {
                primaryBuilder(opts);
            }
            else
            {
                opts.UseNpgsql(options.PrimaryConnectionString, npg =>
                {
                    npg.EnableRetryOnFailure(5);
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", "cnas");
                });
            }
        });
        services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());

        // Read-only context — bound against the (possibly fallback-resolved) replica
        // connection string with the per-replica command timeout applied.
        services.AddDbContext<CnasReadOnlyDbContext>(opts =>
        {
            if (replicaBuilder is not null)
            {
                replicaBuilder(opts);
            }
            else
            {
                opts.UseNpgsql(replicaConnectionString, npg =>
                {
                    npg.EnableRetryOnFailure(5);
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", "cnas");
                    npg.CommandTimeout(options.ReplicaCommandTimeoutSeconds);
                });
            }
        });

        // Capture the timeout so we can apply it through DatabaseFacade for non-Npgsql
        // providers too (e.g. EF Core InMemory in tests). UseNpgsql wires it for the
        // production provider; the post-resolve hook covers everything else.
        var replicaTimeout = options.ReplicaCommandTimeoutSeconds;
        services.AddScoped<IReadOnlyCnasDbContext>(sp =>
        {
            var ctx = sp.GetRequiredService<CnasReadOnlyDbContext>();
            // The InMemory provider rejects SetCommandTimeout — only call it when the
            // provider supports relational operations. Try/catch keeps the seam
            // provider-agnostic without baking provider sniffing into the extension.
            try
            {
                ctx.Database.SetCommandTimeout(replicaTimeout);
            }
            catch (InvalidOperationException)
            {
                // Non-relational provider (InMemory) — nothing to do.
            }
            return ctx;
        });

        return services;
    }

    /// <summary>
    /// Bridges <see cref="CnasDbContextOptions"/> into the established
    /// <see cref="ReadReplicaConfiguration.ResolveConnectionString"/> helper
    /// by adapting the options into an <see cref="IConfiguration"/> snapshot
    /// keyed on the conventional <c>ConnectionStrings:*</c> paths. Keeps the
    /// fallback diagnostic uniform across the two registration paths
    /// (this extension + <see cref="InfrastructureServiceCollectionExtensions.AddCnasInfrastructure"/>).
    /// </summary>
    private static string ResolveReplica(CnasDbContextOptions options, ILoggerFactory loggerFactory)
    {
        var adapter = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = options.PrimaryConnectionString,
                ["ConnectionStrings:PostgresReadReplica"] = options.ReplicaConnectionString,
            })
            .Build();
        return ReadReplicaConfiguration.ResolveConnectionString(adapter, loggerFactory);
    }
}
