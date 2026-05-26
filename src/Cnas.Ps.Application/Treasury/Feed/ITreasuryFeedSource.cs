using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — pluggable adapter that fetches a Treasury feed
/// file for a given <see cref="DateOnly"/>. Implementations cover the
/// in-memory test fixture, the HTTPS placeholder, and (in future iterations)
/// the production SFTP source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash + size are populated by the source.</b> Implementations MUST
/// compute the SHA-256 of the returned bytes and report the byte size — the
/// importer relies on these to populate the
/// <c>TreasuryFeedImport.FileHashSha256</c> + <c>FileSizeBytes</c> columns.
/// </para>
/// <para>
/// <b>SourceReference must be sanitised.</b> URLs MUST NOT carry credentials
/// or tokens; SFTP paths MUST NOT carry passwords; manual filenames MUST NOT
/// carry full local paths. The returned string is persisted verbatim onto
/// <c>TreasuryFeedImport.SourceReference</c> so the boundary is here.
/// </para>
/// </remarks>
public interface ITreasuryFeedSource
{
    /// <summary>
    /// Fetches the feed file covering <paramref name="feedDate"/>.
    /// </summary>
    /// <param name="feedDate">Calendar date the feed should cover.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated outcome envelope; on a missing file
    /// <see cref="ErrorCodes.NotFound"/>; on a configuration miss
    /// <c>TREASURY_FEED.NOT_CONFIGURED</c>; on a transport failure
    /// <see cref="ErrorCodes.Internal"/>.
    /// </returns>
    Task<Result<TreasuryFeedFetchOutcome>> FetchAsync(
        DateOnly feedDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R1810 / TOR BP 1.2-I — outcome envelope returned by an
/// <see cref="ITreasuryFeedSource"/> fetch. Carries the file bytes (already
/// buffered into memory) plus the sanitised provenance metadata the
/// importer needs to persist alongside the run.
/// </summary>
/// <param name="Content">Raw bytes of the feed file, already buffered.</param>
/// <param name="SourceReference">Sanitised source descriptor — URL / SFTP path / upload filename.</param>
/// <param name="SizeBytes">Size of <paramref name="Content"/> in bytes.</param>
/// <param name="HashSha256">Hex-encoded SHA-256 hash of <paramref name="Content"/> (64 lower-case hex chars).</param>
/// <param name="SourceKind">Origin of the returned bytes.</param>
public sealed record TreasuryFeedFetchOutcome(
    byte[] Content,
    string SourceReference,
    long SizeBytes,
    string HashSha256,
    TreasuryFeedSourceKind SourceKind);
