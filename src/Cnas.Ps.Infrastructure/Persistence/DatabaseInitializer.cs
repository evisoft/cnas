using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations at application startup. Idempotent — safe to call
/// from every web instance because PostgreSQL serialises migration application via the
/// <c>__EFMigrationsHistory</c> table.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Resolves a scoped <see cref="CnasDbContext"/> and applies any pending migrations.
    /// </summary>
    public static async Task ApplyMigrationsAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var pendingList = pending.ToArray();
        if (pendingList.Length == 0)
        {
            logger.LogInformation("No pending EF Core migrations.");
            return;
        }

        logger.LogInformation("Applying {Count} pending EF Core migration(s).", pendingList.Length);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
