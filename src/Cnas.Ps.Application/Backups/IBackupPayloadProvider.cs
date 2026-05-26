using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — pluggable provider that produces the binary payload
/// for a backup of a given <see cref="BackupScope"/>. The framework owns no
/// payload semantics; this hook lets a future iteration plug
/// <c>pg_dump</c> / <c>tar</c> / any other producer behind a stable
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>One provider per scope.</b> The orchestrator looks the provider up by
/// <see cref="Scope"/>; misconfigured DI surfaces as a deterministic
/// "no provider registered" failure code (never a thrown exception).
/// </para>
/// <para>
/// <b>SHA-256 included.</b> Providers MUST compute the lowercase-hex
/// SHA-256 digest of the payload they return so the orchestrator can
/// cross-check the target's hash echo without re-reading the payload.
/// </para>
/// </remarks>
public interface IBackupPayloadProvider
{
    /// <summary>Scope this provider handles.</summary>
    BackupScope Scope { get; }

    /// <summary>
    /// Produces the binary payload for one run of the supplied policy.
    /// </summary>
    /// <param name="policy">The policy whose payload to capture.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// A success result carrying the payload + hash + size; a failure when
    /// the producer cannot capture the payload (e.g. <c>pg_dump</c>
    /// unreachable in a future iteration).
    /// </returns>
    Task<Result<BackupPayloadStream>> ProducePayloadAsync(
        BackupPolicy policy,
        CancellationToken cancellationToken = default);
}
