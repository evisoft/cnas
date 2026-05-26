using System.Text.Json;
using Cnas.Ps.Application.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// On-disk implementation of <see cref="IAuditArchive"/>. Each failed batch is
/// serialized to a single JSON file (<c>audit-{utcTimestamp}-{guid}.json</c>)
/// inside <see cref="AuditArchiveOptions.LocalPath"/>; the periodic replay job
/// reads it back, retries the primary flush, and deletes on success. R0188.
/// </summary>
/// <remarks>
/// <para>
/// File contents are written exactly once and never mutated. Read-back is
/// tolerant of concurrent deletion (returns an empty list rather than throwing)
/// so two replay runs racing on the same archive do not raise diagnostics.
/// Malformed JSON is quarantined by renaming with a <c>.corrupt</c> suffix so
/// the replay loop does not livelock on the same poisoned file.
/// </para>
/// <para>
/// Registered as a singleton — the implementation is stateless beyond the
/// configured root directory and is fully thread-safe.
/// </para>
/// </remarks>
public sealed class LocalDiskAuditArchive : IAuditArchive
{
    private readonly AuditArchiveOptions _options;
    private readonly ILogger<LocalDiskAuditArchive> _logger;

    /// <summary>Constructs the archive and ensures the spill directory exists.</summary>
    /// <param name="options">Snapshot of <see cref="AuditArchiveOptions"/>.</param>
    /// <param name="logger">Structured logger.</param>
    public LocalDiskAuditArchive(
        IOptions<AuditArchiveOptions> options,
        ILogger<LocalDiskAuditArchive> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;

        // Eagerly create the directory so the first archive attempt does not
        // need to defend against TOCTOU and so operators see the spill location
        // appear at process start (rather than at first failure).
        try
        {
            Directory.CreateDirectory(_options.LocalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create audit-archive directory {Path}.", _options.LocalPath);
            // Don't throw — the archive will surface the error on the first ArchiveAsync call.
        }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(
        IReadOnlyList<AuditEventRecord> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        // Ensure the directory exists even if construction-time creation failed
        // (e.g. operator created the mount after process start).
        Directory.CreateDirectory(_options.LocalPath);

        var filename = $"audit-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(_options.LocalPath, filename);

        // Serialize directly to file via an array of records. The Application
        // layer's PiiRedactor was invoked at the producer boundary, so the JSON
        // we persist here already complies with SEC 044 / CLAUDE.md §5.6.
        var json = JsonSerializer.Serialize(batch);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Archived {Count} audit records to {Path} after flush failure.",
            batch.Count, path);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchivedAuditBatchRef>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_options.LocalPath))
        {
            return Task.FromResult<IReadOnlyList<ArchivedAuditBatchRef>>(Array.Empty<ArchivedAuditBatchRef>());
        }

        var files = Directory.GetFiles(_options.LocalPath, "audit-*.json");
        var refs = new List<ArchivedAuditBatchRef>(files.Length);
        foreach (var f in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(f);
            // RecordCount is -1 because cheaply computing it would require
            // parsing every file at list time. The replay job reads the file
            // anyway so the count is rediscovered on demand.
            refs.Add(new ArchivedAuditBatchRef(Id: f, CreatedAtUtc: info.CreationTimeUtc, RecordCount: -1));
        }
        return Task.FromResult<IReadOnlyList<ArchivedAuditBatchRef>>(refs);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventRecord>> ReadAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);

        if (!File.Exists(archiveId))
        {
            // Concurrent delete — tolerated, surface as empty so the caller skips.
            return Array.Empty<AuditEventRecord>();
        }

        var json = await File.ReadAllTextAsync(archiveId, cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = JsonSerializer.Deserialize<List<AuditEventRecord>>(json)
                ?? new List<AuditEventRecord>();
            return batch;
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Malformed audit archive at {Path}; quarantining with .corrupt suffix.",
                archiveId);
            try
            {
                File.Move(archiveId, archiveId + ".corrupt", overwrite: true);
            }
            catch (Exception moveEx)
            {
                _logger.LogError(moveEx, "Failed to quarantine malformed audit archive {Path}.", archiveId);
            }
            return Array.Empty<AuditEventRecord>();
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string archiveId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);

        try
        {
            if (File.Exists(archiveId))
            {
                File.Delete(archiveId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete replayed audit archive at {Path}.", archiveId);
        }
        return Task.CompletedTask;
    }
}
