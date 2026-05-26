using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2430 / R2431 / TOR M4 — test / default in-memory implementation of
/// <see cref="IMigrationSource"/>. Holds an in-process dictionary of
/// (PlanCode → list of records) fixtures; integration tests seed the
/// dictionary before invoking the importer. Production swaps this
/// implementation out for the real source adapters in later iterations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safe.</b> The backing store is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> so test fixtures running
/// in parallel can seed disjoint plan codes without locking.
/// </para>
/// <para>
/// <b>SourceKind.</b> Always reports
/// <see cref="MigrationSourceKind.InMemoryTest"/>. The framework's source
/// registry uses this discriminator to match against the plan's
/// <see cref="MigrationPlan.SourceKind"/>.
/// </para>
/// </remarks>
public sealed class InMemoryMigrationSource : IMigrationSource
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<MigrationSourceRecord>> _fixtures = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public MigrationSourceKind SourceKind => MigrationSourceKind.InMemoryTest;

    /// <summary>
    /// Seeds the in-memory dictionary with a record list for the supplied
    /// <paramref name="planCode"/>. Overwrites any previously seeded list.
    /// </summary>
    /// <param name="planCode">Plan code the records belong to.</param>
    /// <param name="records">Source records to expose via <see cref="StreamAsync"/>.</param>
    public void Seed(string planCode, IReadOnlyList<MigrationSourceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(planCode);
        ArgumentNullException.ThrowIfNull(records);
        _fixtures[planCode] = records;
    }

    /// <summary>
    /// Removes the fixture for <paramref name="planCode"/> if present.
    /// </summary>
    /// <param name="planCode">Plan code whose fixture should be cleared.</param>
    public void Clear(string planCode)
    {
        ArgumentNullException.ThrowIfNull(planCode);
        _fixtures.TryRemove(planCode, out _);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MigrationSourceRecord> StreamAsync(
        MigrationPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_fixtures.TryGetValue(plan.PlanCode, out var records))
        {
            yield break;
        }
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Defer to a CompletedTask so the body remains genuinely async.
            await Task.CompletedTask.ConfigureAwait(false);
            yield return record;
        }
    }

    /// <inheritdoc />
    public Task<long> CountAsync(MigrationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_fixtures.TryGetValue(plan.PlanCode, out var records))
        {
            return Task.FromResult(0L);
        }
        return Task.FromResult((long)records.Count);
    }
}
