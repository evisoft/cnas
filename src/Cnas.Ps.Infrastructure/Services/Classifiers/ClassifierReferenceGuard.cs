using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Classifiers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Classifiers;

/// <summary>
/// R0402 / TOR CF 17.09 — pure-read implementation of
/// <see cref="IClassifierReferenceGuard"/>. Counts referencing rows by
/// running one count query per (scheme, citing-entity) pair against the
/// read-replica.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mapping.</b> The known-scheme table is hard-coded inside this class.
/// Each entry pins (a) the canonical scheme code, (b) a human-readable
/// CLR-name label for the citing entity, and (c) a delegate that runs the
/// count query. Schemes that are not in the table count zero references —
/// callers are responsible for extending the mapping when a new scheme is
/// referenced by a new entity column.
/// </para>
/// <para>
/// <b>Read-only contract.</b> The guard injects only
/// <see cref="IReadOnlyCnasDbContext"/> — every count flows through the
/// streaming-replica routed context so the reference-blocking check
/// never adds load to the writable primary.
/// </para>
/// <para>
/// <b>Sensitivity.</b> The guard returns only entity-name + row-count
/// tuples; no row content ever leaves the service. Safe to call from any
/// admin-authenticated path.
/// </para>
/// </remarks>
public sealed class ClassifierReferenceGuard : IClassifierReferenceGuard
{
    /// <summary>Read-replica routed DbContext (per-request scope).</summary>
    private readonly IReadOnlyCnasDbContext _readDb;

    /// <summary>
    /// One row of the hard-coded scheme → (citing-entity, count-delegate)
    /// mapping. Defined as a nested record so unit tests can assert the
    /// shape without poking at the surrounding service.
    /// </summary>
    /// <param name="EntityName">Simple CLR-class label for the citing entity.</param>
    /// <param name="Count">Delegate that returns the row count for the supplied value.</param>
    private sealed record ReferencingEntityMapping(
        string EntityName,
        Func<IReadOnlyCnasDbContext, string, CancellationToken, Task<long>> Count);

    /// <summary>
    /// Canonical scheme code for the Moldovan CAEM Rev. 2 economic-activity
    /// classifier (<c>CAEM</c>). Referenced by <c>Contributor.CaemCode</c>.
    /// </summary>
    public const string SchemeCaem = "CAEM";

    /// <summary>
    /// Canonical scheme code for the form-of-organisation classifier
    /// (<c>CFOJ</c>). Referenced by <c>Contributor.CfojCode</c>.
    /// </summary>
    public const string SchemeCfoj = "CFOJ";

    /// <summary>
    /// Per-scheme dispatch table — the single source of truth for which
    /// entities cite which scheme. Adding a new scheme entails appending
    /// to this dictionary and adding a covering test in the integration
    /// suite. Initialised once at class-load time; immutable thereafter.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ReferencingEntityMapping>> Mappings =
        new Dictionary<string, IReadOnlyList<ReferencingEntityMapping>>(StringComparer.Ordinal)
        {
            [SchemeCaem] = new ReferencingEntityMapping[]
            {
                new(
                    EntityName: nameof(Cnas.Ps.Core.Domain.Contributor),
                    Count: static (db, value, ct) =>
                        db.Contributors
                          .Where(c => c.CaemCode == value)
                          .LongCountAsync(ct)),
            },
            [SchemeCfoj] = new ReferencingEntityMapping[]
            {
                new(
                    EntityName: nameof(Cnas.Ps.Core.Domain.Contributor),
                    Count: static (db, value, ct) =>
                        db.Contributors
                          .Where(c => c.CfojCode == value)
                          .LongCountAsync(ct)),
            },
        };

    /// <summary>
    /// Constructs the guard with its single dependency.
    /// </summary>
    /// <param name="readDb">Read-replica routed DbContext (per-request scope).</param>
    public ClassifierReferenceGuard(IReadOnlyCnasDbContext readDb)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        _readDb = readDb;
    }

    /// <inheritdoc />
    public async Task<Result<ClassifierReferenceScanResultDto>> ScanAsync(
        string schemeCode,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // Canonical scheme key — trim + invariant-uppercase so callers that
        // pass mixed-case scheme strings still hit the dispatch table.
        var canonicalScheme = schemeCode.Trim().ToUpperInvariant();

        if (!Mappings.TryGetValue(canonicalScheme, out var entityMappings))
        {
            // Unknown scheme — by design we return zero references rather
            // than fail. The integration suite asserts the expected mappings
            // exist; new schemes hit this branch until a maintainer extends
            // the dispatch table above (see remarks).
            return Result<ClassifierReferenceScanResultDto>.Success(
                new ClassifierReferenceScanResultDto(
                    SchemeCode: canonicalScheme,
                    Value: value,
                    ReferencingRowCount: 0,
                    ReferencingEntities: Array.Empty<ClassifierReferencingEntityDto>()));
        }

        // Per-entity counts. Sequential — the entity list is small (≤ 3
        // per scheme today) and parallelising would force us to take a
        // dependency on DbContext-per-query which the read-replica context
        // is not designed for.
        var perEntity = new List<ClassifierReferencingEntityDto>(entityMappings.Count);
        long total = 0;
        foreach (var mapping in entityMappings)
        {
            var count = await mapping.Count(_readDb, value, cancellationToken).ConfigureAwait(false);
            perEntity.Add(new ClassifierReferencingEntityDto(mapping.EntityName, count));
            total += count;
        }

        return Result<ClassifierReferenceScanResultDto>.Success(
            new ClassifierReferenceScanResultDto(
                SchemeCode: canonicalScheme,
                Value: value,
                ReferencingRowCount: total,
                ReferencingEntities: perEntity));
    }
}
