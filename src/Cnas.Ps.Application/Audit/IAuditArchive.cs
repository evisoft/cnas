namespace Cnas.Ps.Application.Audit;

/// <summary>
/// Durable spill area for audit batches whose primary flush failed.
/// SEC 038-048 require audit to survive transient backend outages; the archive
/// is the at-rest store of unflushed batches that a periodic replay job retries
/// until the primary flush succeeds.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to be idempotent and tolerant of concurrent replays.
/// A pending archive is identified by an opaque string id (the implementation may use
/// a filename, an object key, or anything else stable enough to address a single batch).
/// </para>
/// <para>
/// R0188 ships the local-disk implementation
/// (<c>LocalDiskAuditArchive</c> in the Infrastructure layer); a future iteration may
/// add a MinIO-backed implementation that mirrors the same contract.
/// </para>
/// </remarks>
public interface IAuditArchive
{
    /// <summary>
    /// Persists a batch of audit records so a later replay can retry insertion.
    /// </summary>
    /// <remarks>
    /// Implementations MUST NOT mutate the batch payload — the JSON shape is the
    /// already-PII-redacted record exactly as it was about to be written to the
    /// primary store. Passing an empty list is a no-op.
    /// </remarks>
    /// <param name="batch">Records to spill to durable storage; never <c>null</c>.</param>
    /// <param name="cancellationToken">Observed during the underlying write.</param>
    Task ArchiveAsync(
        IReadOnlyList<AuditEventRecord> batch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates archive ids the replay job should attempt. The list is a snapshot
    /// taken at call time; concurrent writers may add more after the read.
    /// </summary>
    /// <param name="cancellationToken">Observed during enumeration.</param>
    /// <returns>An opaque, possibly empty, list of references the caller can replay.</returns>
    Task<IReadOnlyList<ArchivedAuditBatchRef>> ListPendingAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the records for a given archive id. Returns an empty list if the
    /// archive vanished concurrently (another replay won the race) or if the
    /// payload was malformed and quarantined.
    /// </summary>
    /// <param name="archiveId">Opaque id returned by <see cref="ListPendingAsync"/>.</param>
    /// <param name="cancellationToken">Observed during the underlying read.</param>
    /// <returns>The originally-archived batch, or an empty list when not available.</returns>
    Task<IReadOnlyList<AuditEventRecord>> ReadAsync(
        string archiveId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an archive once its contents have been replayed successfully.
    /// Deleting a missing id is a tolerated no-op (idempotent).
    /// </summary>
    /// <param name="archiveId">Opaque id returned by <see cref="ListPendingAsync"/>.</param>
    /// <param name="cancellationToken">Observed during the underlying delete.</param>
    Task DeleteAsync(
        string archiveId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Opaque reference to an archived audit batch; the <see cref="Id"/> is
/// implementation-specific (filename, object key, …) and should be treated as
/// opaque by callers.
/// </summary>
/// <param name="Id">Opaque, implementation-specific identifier.</param>
/// <param name="CreatedAtUtc">UTC timestamp at which the archive was first persisted.</param>
/// <param name="RecordCount">
/// Number of records inside, or <c>-1</c> when the implementation does not cheaply
/// know the count (the replay job reads the file anyway).
/// </param>
public sealed record ArchivedAuditBatchRef(
    string Id,
    DateTime CreatedAtUtc,
    int RecordCount);
