using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — Singleton dispatcher that selects the right
/// <see cref="IProfileManagementStrategy"/> for an inbound profile mutation
/// and invokes <see cref="IProfileManagementStrategy.ApplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime hygiene.</b> The dispatcher is registered Singleton (no
/// per-request state); it captures the root
/// <see cref="IServiceScopeFactory"/> and resolves the SCOPED strategy
/// collaborators inside a per-dispatch
/// <see cref="IServiceScope"/>. That way the singleton never captures a
/// scoped collaborator (a CLAUDE.md DI-lifetime hazard the architecture
/// tests guard against).
/// </para>
/// <para>
/// <b>Unknown key.</b> When no registered strategy matches the supplied
/// key the dispatcher returns <see cref="ErrorCodes.NotFound"/> with a
/// message naming the unresolvable key.
/// </para>
/// </remarks>
/// <param name="scopeFactory">DI scope factory used to resolve strategies per dispatch.</param>
public sealed class ProfileManagementStrategyDispatcher(IServiceScopeFactory scopeFactory)
    : IProfileManagementStrategyDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <inheritdoc />
    public async Task<Result<ProfileOutput>> DispatchAsync(
        string strategyKey,
        string profileSqid,
        ProfileManagementInput input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(strategyKey))
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.ValidationFailed,
                "Strategy key is required.");
        }
        ArgumentNullException.ThrowIfNull(input);

        // Resolve in a per-dispatch scope so the singleton dispatcher never
        // captures a scoped collaborator.
        using var scope = _scopeFactory.CreateScope();
        var strategies = scope.ServiceProvider
            .GetServices<IProfileManagementStrategy>()
            .ToList();
        var strategy = strategies.FirstOrDefault(
            s => string.Equals(s.StrategyKey, strategyKey, StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
        {
            return Result<ProfileOutput>.Failure(
                ErrorCodes.NotFound,
                $"No profile-management strategy registered for key '{strategyKey}'.");
        }

        return await strategy
            .ApplyAsync(profileSqid ?? string.Empty, input, cancellationToken)
            .ConfigureAwait(false);
    }
}
