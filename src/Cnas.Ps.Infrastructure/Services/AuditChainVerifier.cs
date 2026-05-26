using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0194 / SEC 047 — verifies the SHA-256 hash chain that the audit pipeline
/// writes across every <see cref="Cnas.Ps.Core.Domain.AuditLog"/> row. Reads
/// rows from the streaming replica via <see cref="IReadOnlyCnasDbContext"/>
/// because the check is pure-read; the verifier never mutates the chain.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm: stream rows in <c>AuditLog.Id</c> ascending. For each row check
/// (a) <see cref="Cnas.Ps.Core.Domain.AuditLog.PrevHash"/> equals the previous
/// row's <see cref="Cnas.Ps.Core.Domain.AuditLog.RowHash"/> (or the literal
/// <c>"GENESIS"</c> for the very first row), and
/// (b) the recomputed digest from <see cref="AuditFlushProjector.ComputeRowHash"/>
/// equals the stored <see cref="Cnas.Ps.Core.Domain.AuditLog.RowHash"/>.
/// The walk stops at the first failure and reports the offending row id plus a
/// stable reason code.
/// </para>
/// <para>
/// Materialised to a list rather than streamed via <c>AsAsyncEnumerable</c> so
/// the EF Core InMemory provider — which does not implement the async-stream
/// shim — keeps working under the existing test fixtures. Production volumes
/// remain modest (a few million rows over a multi-year retention window) and
/// the verifier is invoked on an ops cadence, not a hot path.
/// </para>
/// </remarks>
public sealed class AuditChainVerifier : IAuditChainVerifier
{
    private const string GenesisAnchor = "GENESIS";

    private readonly IReadOnlyCnasDbContext _db;

    /// <summary>Constructs the verifier with its read-only context dependency.</summary>
    /// <param name="db">Read-only context routed to the streaming replica (R0026).</param>
    public AuditChainVerifier(IReadOnlyCnasDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Result<AuditChainVerificationReport>> VerifyAsync(CancellationToken cancellationToken = default)
    {
        // Project to the exact set of columns the canonical-form recipe needs
        // — avoids paying for any unrelated columns we may add later. Order by
        // Id so the chain is walked in the same order it was inserted.
        var rows = await _db.AuditLogs
            .OrderBy(a => a.Id)
            .Select(a => new VerifierRow(
                a.Id,
                a.EventAtUtc,
                a.Severity,
                a.EventCode,
                a.ActorId,
                a.TargetEntity,
                a.TargetEntityId,
                a.SourceIp,
                a.CorrelationId,
                a.DetailsJson,
                a.PrevHash,
                a.RowHash))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var prev = GenesisAnchor;
        long checkedCount = 0;
        foreach (var row in rows)
        {
            checkedCount++;

            if (!string.Equals(row.PrevHash, prev, StringComparison.Ordinal))
            {
                // R0040 — failed verification; report the outcome via the chain.valid
                // tag so dashboards can chart broken-chain rate without scraping logs.
                CnasMeter.AuditChainVerified.Add(1,
                    new KeyValuePair<string, object?>("chain.valid", false));
                return Result<AuditChainVerificationReport>.Success(
                    new AuditChainVerificationReport(
                        IsValid: false,
                        CheckedCount: checkedCount,
                        FirstBrokenRowId: row.Id,
                        FirstBrokenReason: "PrevHashMismatch"));
            }

            var expected = AuditFlushProjector.ComputeRowHash(
                new AuditEventRecord(
                    EventCode: row.EventCode,
                    Severity: row.Severity,
                    ActorId: row.ActorId,
                    TargetEntity: row.TargetEntity,
                    TargetEntityId: row.TargetEntityId,
                    DetailsJson: row.DetailsJson,
                    SourceIp: row.SourceIp,
                    CorrelationId: row.CorrelationId,
                    EventAtUtc: row.EventAtUtc),
                prev);

            if (!string.Equals(row.RowHash, expected, StringComparison.Ordinal))
            {
                // R0040 — same broken-chain accounting as the PrevHashMismatch branch
                // above; both failure modes share the chain.valid=false metric so a
                // single chart surfaces the headline "chain integrity" signal.
                CnasMeter.AuditChainVerified.Add(1,
                    new KeyValuePair<string, object?>("chain.valid", false));
                return Result<AuditChainVerificationReport>.Success(
                    new AuditChainVerificationReport(
                        IsValid: false,
                        CheckedCount: checkedCount,
                        FirstBrokenRowId: row.Id,
                        FirstBrokenReason: "RowHashMismatch"));
            }

            prev = row.RowHash;
        }

        // R0040 — happy-path verification; tag chain.valid=true so the operator
        // dashboard charts a clean run rate alongside the broken-chain rate.
        CnasMeter.AuditChainVerified.Add(1,
            new KeyValuePair<string, object?>("chain.valid", true));
        return Result<AuditChainVerificationReport>.Success(
            new AuditChainVerificationReport(
                IsValid: true,
                CheckedCount: checkedCount,
                FirstBrokenRowId: null,
                FirstBrokenReason: null));
    }

    /// <summary>
    /// Compact projection of <see cref="Cnas.Ps.Core.Domain.AuditLog"/> carrying
    /// only the columns the chain recipe needs. Local to the verifier so the
    /// EF projection is type-checked at compile time.
    /// </summary>
    private sealed record VerifierRow(
        long Id,
        DateTime EventAtUtc,
        Cnas.Ps.Core.Domain.AuditSeverity Severity,
        string EventCode,
        string ActorId,
        string? TargetEntity,
        long? TargetEntityId,
        string? SourceIp,
        string? CorrelationId,
        string DetailsJson,
        string PrevHash,
        string RowHash);
}
