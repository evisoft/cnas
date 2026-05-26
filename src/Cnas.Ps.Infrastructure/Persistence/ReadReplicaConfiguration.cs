using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// R0026 — connection-string resolution for the Postgres read-replica DI wiring.
/// Centralises the "use the replica, or fall back loudly to the primary" decision
/// so the same policy is exercised in both production startup and unit tests.
/// </summary>
/// <remarks>
/// <para>
/// Two configuration keys drive the routing:
/// <list type="bullet">
///   <item><c>ConnectionStrings:Postgres</c> — primary read/write endpoint.</item>
///   <item><c>ConnectionStrings:PostgresReadReplica</c> — read-only replica
///         (Postgres streaming replication per TOR PSR 006 / ARH 025).</item>
/// </list>
/// </para>
/// <para>
/// When the replica connection string is unset, the helper returns the primary
/// connection string and emits a WARN-level log line through the supplied
/// <see cref="ILoggerFactory"/>. This is acceptable for dev (single Postgres)
/// but a misconfiguration in production — surfacing it loudly prevents silent
/// loss of replica isolation. The fallback NEVER throws: a host with only a
/// primary endpoint configured boots successfully so the rest of the system
/// can still run.
/// </para>
/// </remarks>
public static class ReadReplicaConfiguration
{
    /// <summary>
    /// Stable category name used for the fallback WARN log line. Tests assert
    /// on the message content rather than the category, but a stable name keeps
    /// operator log dashboards readable.
    /// </summary>
    public const string LoggerCategory = "Cnas.Ps.Infrastructure.ReadReplica";

    /// <summary>
    /// Resolves the read-replica connection string with primary fallback. When
    /// <c>ConnectionStrings:PostgresReadReplica</c> is set returns it verbatim;
    /// otherwise returns <c>ConnectionStrings:Postgres</c> and emits a WARN log
    /// line. Throws <see cref="InvalidOperationException"/> when BOTH are unset
    /// because at that point the host genuinely cannot connect to any database
    /// and a deferred failure at first-query-time is harder to diagnose.
    /// </summary>
    /// <param name="configuration">
    /// Root configuration. Looked up via
    /// <see cref="ConfigurationExtensions.GetConnectionString(IConfiguration, string)"/>
    /// so both <c>ConnectionStrings:*</c> and the flat <c>*</c> conventions work.
    /// </param>
    /// <param name="loggerFactory">
    /// Factory used to create the <see cref="ILogger"/> that emits the WARN line.
    /// Tests pass a capturing factory; production uses the host's default factory.
    /// </param>
    /// <returns>The replica connection string when configured, otherwise the primary.</returns>
    /// <exception cref="InvalidOperationException">
    /// Both <c>ConnectionStrings:Postgres</c> and <c>ConnectionStrings:PostgresReadReplica</c>
    /// are unset — a host with no database at all cannot start.
    /// </exception>
    /// <example>
    /// ResolveConnectionString(config with PostgresReadReplica set)         → replica string, no log
    /// ResolveConnectionString(config with only Postgres set)               → primary string, WARN logged
    /// ResolveConnectionString(config with neither set)                     → throws
    /// </example>
    public static string ResolveConnectionString(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var replica = configuration.GetConnectionString("PostgresReadReplica");
        if (!string.IsNullOrWhiteSpace(replica))
        {
            return replica;
        }

        // Fall back to the primary. We log first so even hosts that fail at
        // the next line (no primary either) still leave breadcrumbs in the
        // diagnostic log explaining why the replica path could not be taken.
        var logger = loggerFactory.CreateLogger(LoggerCategory);
        logger.LogWarning(
            "ConnectionStrings:PostgresReadReplica is unset; read-only context will route to the primary. " +
            "This is acceptable for dev but should NOT be the case in production (TOR PSR 006).");

        var primary = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(primary))
        {
            throw new InvalidOperationException(
                "Neither ConnectionStrings:Postgres nor ConnectionStrings:PostgresReadReplica is set — " +
                "the host cannot resolve a database endpoint.");
        }
        return primary;
    }
}
