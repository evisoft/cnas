using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant: for every <see cref="TreasuryPaymentReceipt"/>,
/// the persisted <c>UndistributedRemainderAmount</c> must be consistent with
/// the distribution status:
/// <list type="bullet">
///   <item><see cref="TreasuryPaymentDistributionStatus.Distributed"/> ⇒ remainder == 0 or null.</item>
///   <item><see cref="TreasuryPaymentDistributionStatus.Failed"/> ⇒ remainder == AmountReceived.</item>
///   <item><see cref="TreasuryPaymentDistributionStatus.PartiallyDistributed"/> ⇒ 0 &lt; remainder &lt; AmountReceived.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This check operates on the receipt row itself because the codebase does
/// NOT yet ship a per-receipt <c>TreasuryReceiptDistribution</c> aggregate.
/// If/when that aggregate lands, this check should be replaced with the
/// stronger "sum of distributions equals receipt amount" invariant declared
/// in the SEC 036 spec.
/// </para>
/// </remarks>
public sealed class TreasuryReceiptAmountSumCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "TREASURY_RECEIPT.AMOUNT_MISMATCH";

    /// <inheritdoc />
    public string AggregateName => nameof(TreasuryPaymentReceipt);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.High;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var rows = await ctx.Db.TreasuryPaymentReceipts
            .Where(r => r.IsActive)
            .Select(r => new { r.Id, r.AmountReceived, r.DistributionStatus, r.UndistributedRemainderAmount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var row in rows)
        {
            var remainder = row.UndistributedRemainderAmount ?? 0m;
            string? mismatch = row.DistributionStatus switch
            {
                TreasuryPaymentDistributionStatus.Distributed when remainder != 0m
                    => "Distributed status expects remainder = 0",
                TreasuryPaymentDistributionStatus.Failed when remainder != row.AmountReceived
                    => $"Failed status expects remainder = AmountReceived ({row.AmountReceived})",
                TreasuryPaymentDistributionStatus.PartiallyDistributed when remainder <= 0m || remainder >= row.AmountReceived
                    => $"PartiallyDistributed status expects 0 < remainder < AmountReceived ({row.AmountReceived})",
                _ => null,
            };
            if (mismatch is not null)
            {
                findings.Add(new IntegrityCheckFindingRecord(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: row.Id,
                    Description: $"TreasuryPaymentReceipt remainder is incoherent with its distribution status: {mismatch}.",
                    ExpectedValue: mismatch,
                    ActualValue: $"DistributionStatus={row.DistributionStatus}; AmountReceived={row.AmountReceived}; Remainder={remainder}"));
            }
        }

        return new IntegrityCheckPartialResult(rows.Count, findings);
    }
}
