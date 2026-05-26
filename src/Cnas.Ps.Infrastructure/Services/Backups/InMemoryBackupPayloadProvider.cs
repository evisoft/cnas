using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — deterministic in-memory
/// <see cref="IBackupPayloadProvider"/> registered for every
/// <see cref="BackupScope"/>. Returns a tiny byte blob shaped like
/// <c>"CNAS-BACKUP|{policyCode}|{scope}"</c> so tests can assert hash
/// stability without spinning up real producers. Production swaps in
/// scope-specific concrete providers (pg_dump etc.) in a later iteration.
/// </summary>
public sealed class InMemoryBackupPayloadProvider : IBackupPayloadProvider
{
    private readonly BackupScope _scope;

    /// <summary>Constructs a provider for the supplied <paramref name="scope"/>.</summary>
    /// <param name="scope">Scope this provider produces payloads for.</param>
    public InMemoryBackupPayloadProvider(BackupScope scope)
    {
        _scope = scope;
    }

    /// <inheritdoc />
    public BackupScope Scope => _scope;

    /// <inheritdoc />
    public Task<Result<BackupPayloadStream>> ProducePayloadAsync(
        BackupPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();

        var marker = $"CNAS-BACKUP|{policy.PolicyCode}|{_scope}";
        var bytes = Encoding.UTF8.GetBytes(marker);
        var hash = InMemoryBackupTarget.ComputeSha256Hex(bytes);
        var stream = new BackupPayloadStream(bytes, hash, bytes.LongLength);
        return Task.FromResult(Result<BackupPayloadStream>.Success(stream));
    }
}
