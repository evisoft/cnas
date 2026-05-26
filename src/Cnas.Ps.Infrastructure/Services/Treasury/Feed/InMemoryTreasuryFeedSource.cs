using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — test / default in-memory implementation of
/// <see cref="ITreasuryFeedSource"/>. Holds an in-process dictionary of
/// (FeedDate → byte[]) fixtures; integration tests seed the dictionary
/// before invoking the importer. Production swaps this implementation out
/// for the HTTPS / SFTP source via the host's
/// <c>UseHttpsTreasuryFeedSource</c> extension.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safe.</b> The backing store is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> so test fixtures running
/// in parallel can seed disjoint dates without locking.
/// </para>
/// <para>
/// <b>Hash + size are deterministic.</b> Fixtures supplied to <see cref="Seed"/>
/// produce a fixed SHA-256 — re-seeding the same date with the same bytes is
/// safe and idempotent.
/// </para>
/// </remarks>
public sealed class InMemoryTreasuryFeedSource : ITreasuryFeedSource
{
    private readonly ConcurrentDictionary<DateOnly, byte[]> _fixtures = new();

    /// <summary>
    /// Seeds the in-memory dictionary with a feed file for
    /// <paramref name="feedDate"/>. Overwrites any previously seeded bytes.
    /// </summary>
    /// <param name="feedDate">Calendar date the feed covers.</param>
    /// <param name="content">Feed-file bytes (typically UTF-8 CSV).</param>
    public void Seed(DateOnly feedDate, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _fixtures[feedDate] = content;
    }

    /// <summary>
    /// Removes the fixture for <paramref name="feedDate"/> if present.
    /// </summary>
    /// <param name="feedDate">Calendar date the feed covers.</param>
    public void Clear(DateOnly feedDate)
    {
        _fixtures.TryRemove(feedDate, out _);
    }

    /// <inheritdoc />
    public Task<Result<TreasuryFeedFetchOutcome>> FetchAsync(
        DateOnly feedDate,
        CancellationToken cancellationToken = default)
    {
        if (!_fixtures.TryGetValue(feedDate, out var bytes))
        {
            return Task.FromResult(Result<TreasuryFeedFetchOutcome>.Failure(
                ErrorCodes.NotFound,
                "No Treasury feed fixture seeded for the requested date."));
        }

        // Defensive copy — callers must not be able to mutate the seeded fixture.
        var copy = new byte[bytes.LongLength];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);

        var hash = ComputeSha256Hex(copy);
        var reference = "in-memory-fixture:" + feedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var outcome = new TreasuryFeedFetchOutcome(
            Content: copy,
            SourceReference: reference,
            SizeBytes: copy.LongLength,
            HashSha256: hash,
            SourceKind: TreasuryFeedSourceKind.InMemoryTest);
        return Task.FromResult(Result<TreasuryFeedFetchOutcome>.Success(outcome));
    }

    /// <summary>
    /// Computes the lower-case hex SHA-256 of <paramref name="bytes"/>.
    /// </summary>
    /// <param name="bytes">Byte array to hash.</param>
    /// <returns>64-character lower-case hex string.</returns>
    private static string ComputeSha256Hex(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        var sb = new StringBuilder(digest.Length * 2);
        for (int i = 0; i < digest.Length; i++)
        {
            sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
