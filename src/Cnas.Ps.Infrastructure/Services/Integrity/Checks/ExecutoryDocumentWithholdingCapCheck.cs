using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant: for every <see cref="ExecutoryDocument"/>
/// with a set <c>TotalOwedMdl</c>, the running <c>TotalWithheldMdl</c> tally
/// MUST be less than or equal to <c>TotalOwedMdl</c>. A breach indicates a
/// double-credit bug (the calculator allocated more than the debt owed).
/// </summary>
public sealed class ExecutoryDocumentWithholdingCapCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "EXECUTORY_DOC.WITHHOLDING_OVERFLOW";

    /// <inheritdoc />
    public string AggregateName => nameof(ExecutoryDocument);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.High;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Skip rows whose TotalOwedMdl is null (open-ended obligations have
        // no cap to enforce).
        var rows = await ctx.Db.ExecutoryDocuments
            .Where(d => d.IsActive && d.TotalOwedMdl != null)
            .Select(d => new { d.Id, d.TotalOwedMdl, d.TotalWithheldMdl })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var row in rows)
        {
            // Guard against null even though the WHERE clause excluded null —
            // EF Core projection narrows the type but the lifted value is
            // still decimal? per the entity definition.
            if (row.TotalOwedMdl is not { } cap)
            {
                continue;
            }
            if (row.TotalWithheldMdl > cap)
            {
                var overflow = row.TotalWithheldMdl - cap;
                findings.Add(new IntegrityCheckFindingRecord(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: row.Id,
                    Description: "ExecutoryDocument.TotalWithheldMdl exceeds TotalOwedMdl cap.",
                    ExpectedValue: $"TotalWithheldMdl <= {cap}",
                    ActualValue: $"TotalWithheldMdl={row.TotalWithheldMdl}; OverflowDelta={overflow}"));
            }
        }

        return new IntegrityCheckPartialResult(rows.Count, findings);
    }
}
