using System.Collections.Concurrent;
using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — test / default in-memory implementation of
/// <see cref="IExternalSourceConnector"/>. Holds an in-process dictionary of
/// (SourceCode, AsOfDate → ExternalSourceFetchOutcomeDto) fixtures; tests seed
/// the dictionary before invoking the ingestion service. Production replaces
/// this implementation per-source via concrete connectors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wildcard SourceCode.</b> The default instance accepts any source code
/// — the dictionary key carries the source code so seeded fixtures stay
/// per-source. This makes the in-memory connector a safe fallback when an
/// unconfigured source code is requested.
/// </para>
/// <para>
/// <b>Thread-safe.</b> The backing store is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> so test fixtures running
/// in parallel can seed disjoint (source, date) pairs without locking.
/// </para>
/// </remarks>
public sealed class InMemoryExternalSourceConnector : IExternalSourceConnector
{
    /// <summary>
    /// Reserved source-code value indicating this connector matches any code.
    /// Mirrors the wildcard pattern used by <c>IdentityMigrationRecordMapper</c>.
    /// </summary>
    public const string WildcardSourceCode = "*";

    private readonly ConcurrentDictionary<string, ExternalSourceFetchOutcomeDto> _fixtures = new(
        StringComparer.Ordinal);

    /// <inheritdoc />
    public string SourceCode => WildcardSourceCode;

    /// <summary>
    /// Seeds the in-memory dictionary with a canned outcome for the
    /// <paramref name="sourceCode"/> + <paramref name="asOfDate"/> tuple.
    /// Overwrites any previously seeded entry.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">As-of date the outcome covers.</param>
    /// <param name="outcome">Canned outcome envelope.</param>
    public void Seed(string sourceCode, DateOnly asOfDate, ExternalSourceFetchOutcomeDto outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        ArgumentNullException.ThrowIfNull(outcome);
        _fixtures[BuildKey(sourceCode, asOfDate)] = outcome;
    }

    /// <summary>Removes the seeded fixture for the supplied tuple if present.</summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">As-of date the outcome covers.</param>
    public void Clear(string sourceCode, DateOnly asOfDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        _fixtures.TryRemove(BuildKey(sourceCode, asOfDate), out _);
    }

    /// <inheritdoc />
    public Task<Result<ExternalSourceFetchOutcomeDto>> FetchAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
        => FetchAsync(WildcardSourceCode, asOfDate, cancellationToken);

    /// <summary>
    /// Source-aware overload used by the ingestion service when the in-memory
    /// connector is selected as the fallback for a specific source code.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">As-of date the outcome covers.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The seeded outcome envelope, or a deterministic empty outcome.</returns>
    public Task<Result<ExternalSourceFetchOutcomeDto>> FetchAsync(
        string sourceCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        if (_fixtures.TryGetValue(BuildKey(sourceCode, asOfDate), out var seeded))
        {
            return Task.FromResult(Result<ExternalSourceFetchOutcomeDto>.Success(seeded));
        }

        // Default deterministic outcome — zero records pulled. Keeps the
        // ingestion service's lifecycle exerciseable end-to-end without
        // forcing every test to seed fixtures.
        var empty = new ExternalSourceFetchOutcomeDto(
            RecordsPulled: 0,
            RecordsApplied: 0,
            RecordsSkipped: 0,
            RecordsFailed: 0,
            UpstreamPullId: $"in-memory:{sourceCode}:{asOfDate:O}");
        return Task.FromResult(Result<ExternalSourceFetchOutcomeDto>.Success(empty));
    }

    /// <summary>Builds the dictionary key from the (source, date) tuple.</summary>
    /// <param name="sourceCode">Upper-case source code.</param>
    /// <param name="asOfDate">As-of date.</param>
    /// <returns>Stable dictionary key string.</returns>
    private static string BuildKey(string sourceCode, DateOnly asOfDate)
        => $"{sourceCode}|{asOfDate:yyyy-MM-dd}";
}
