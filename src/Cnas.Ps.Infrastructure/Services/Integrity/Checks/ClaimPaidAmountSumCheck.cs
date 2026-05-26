using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant: for every <see cref="Claim"/>, the
/// persisted <c>PaidAmount</c> column must equal the sum of its non-deleted
/// <see cref="ClaimPayment"/> children. A divergence indicates a partial
/// failure during payment registration or a manual data correction that
/// missed the running total.
/// </summary>
public sealed class ClaimPaidAmountSumCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "CLAIM.RUNNING_TOTAL_MISMATCH";

    /// <inheritdoc />
    public string AggregateName => nameof(Claim);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.High;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Pull all live claims into memory then compare each row's persisted
        // PaidAmount with the recomputed sum from its children. The dataset
        // is bounded (one row per outstanding obligation) so this scan stays
        // cheap; if it grows we can switch to a GROUP BY join.
        var claims = await ctx.Db.Claims
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.PaidAmount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var paymentSums = await ctx.Db.ClaimPayments
            .Where(p => p.IsActive)
            .GroupBy(p => p.ClaimId)
            .Select(g => new { ClaimId = g.Key, Sum = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sumByClaim = paymentSums.ToDictionary(x => x.ClaimId, x => x.Sum);

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var claim in claims)
        {
            var expected = sumByClaim.TryGetValue(claim.Id, out var sum) ? sum : 0m;
            if (expected != claim.PaidAmount)
            {
                findings.Add(new IntegrityCheckFindingRecord(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: claim.Id,
                    Description: "Claim.PaidAmount diverges from the sum of its ClaimPayment children.",
                    ExpectedValue: expected.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    ActualValue: claim.PaidAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return new IntegrityCheckPartialResult(claims.Count, findings);
    }
}
