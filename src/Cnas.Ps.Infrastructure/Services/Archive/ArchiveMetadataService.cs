using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Archive;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Archive;

/// <summary>
/// R0332 / TOR CF 12.02 — read-replica implementation of
/// <see cref="IArchiveMetadataService"/>. Issues five depersonalised COUNT
/// queries against <see cref="IReadOnlyCnasDbContext"/> and stitches them
/// into a single <see cref="ArchiveSummaryDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-replica routing.</b> Per R0026 / PSR 006 / ARH 025 every read
/// aggregation flows through <see cref="IReadOnlyCnasDbContext"/>. The
/// <c>ReadReplicaLayeringTests</c> architecture suite asserts this
/// dependency-direction invariant at build time.
/// </para>
/// <para>
/// <b>Decisions tab.</b> The TOR groups "Decizii" (decision documents) as a
/// distinct register inside the archive even though they share a table with
/// other documents. The summariser filters
/// <see cref="IReadOnlyCnasDbContext.Documents"/> by
/// <see cref="DocumentKind.Decision"/> for that tab, and counts the entire
/// table for the broader "Documents" tab.
/// </para>
/// </remarks>
/// <param name="readDb">Read-replica EF Core context (R0026).</param>
public sealed class ArchiveMetadataService(IReadOnlyCnasDbContext readDb) : IArchiveMetadataService
{
    /// <summary>Stable tab discriminator: Annex 1 contributors register.</summary>
    public const string ContributorsTabCode = "contributors";

    /// <summary>Stable tab discriminator: Annex 2 insured-persons register.</summary>
    public const string InsuredPersonsTabCode = "insured-persons";

    /// <summary>Stable tab discriminator: emitted decision documents.</summary>
    public const string DecisionsTabCode = "decisions";

    /// <summary>Stable tab discriminator: dossier register.</summary>
    public const string DossiersTabCode = "dossiers";

    /// <summary>Stable tab discriminator: full document register.</summary>
    public const string DocumentsTabCode = "documents";

    private readonly IReadOnlyCnasDbContext _readDb = readDb;

    /// <inheritdoc />
    public async Task<Result<ArchiveSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        // The five tabs each need three numbers: active count, archived count,
        // last-update timestamp. We materialise them sequentially because the
        // EF Core DbContext is not thread-safe — issuing the queries on the
        // same context from parallel awaits would risk concurrency exceptions.
        var contributors = await SummariseAsync(
            ContributorsTabCode,
            _readDb.Contributors,
            cancellationToken).ConfigureAwait(false);
        var insured = await SummariseAsync(
            InsuredPersonsTabCode,
            _readDb.InsuredPersons,
            cancellationToken).ConfigureAwait(false);
        var decisions = await SummariseAsync(
            DecisionsTabCode,
            _readDb.Documents.Where(d => d.Kind == DocumentKind.Decision),
            cancellationToken).ConfigureAwait(false);
        var dossiers = await SummariseAsync(
            DossiersTabCode,
            _readDb.Dossiers,
            cancellationToken).ConfigureAwait(false);
        var documents = await SummariseAsync(
            DocumentsTabCode,
            _readDb.Documents,
            cancellationToken).ConfigureAwait(false);

        return Result<ArchiveSummaryDto>.Success(
            new ArchiveSummaryDto(contributors, insured, decisions, dossiers, documents));
    }

    /// <summary>
    /// Counts active vs archived rows in <paramref name="source"/> and finds
    /// the most-recently-touched row's timestamp. Pulls
    /// <c>UpdatedAtUtc ?? CreatedAtUtc</c> as the "last touched" datum so
    /// inserts also bump the badge.
    /// </summary>
    /// <typeparam name="T">Auditable entity type (CLR class).</typeparam>
    /// <param name="tabCode">Stable tab discriminator.</param>
    /// <param name="source">Read-only queryable backing the tab.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>A populated <see cref="ArchiveTabSummaryDto"/>.</returns>
    private static async Task<ArchiveTabSummaryDto> SummariseAsync<T>(
        string tabCode,
        IQueryable<T> source,
        CancellationToken cancellationToken)
        where T : AuditableEntity
    {
        var totalActive = await source.LongCountAsync(e => e.IsActive, cancellationToken).ConfigureAwait(false);
        var totalArchived = await source.LongCountAsync(e => !e.IsActive, cancellationToken).ConfigureAwait(false);
        var lastUpdated = await source
            .Select(e => (System.DateTime?)(e.UpdatedAtUtc ?? e.CreatedAtUtc))
            .OrderByDescending(d => d)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveTabSummaryDto(tabCode, totalActive, totalArchived, lastUpdated);
    }
}
