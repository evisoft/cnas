using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariants on the <see cref="Claim"/> state machine:
/// <list type="bullet">
///   <item><see cref="ClaimStatus.Settled"/> ⇒ <c>RemainingAmount == 0</c> AND <c>SettledDate IS NOT NULL</c>.</item>
///   <item><see cref="ClaimStatus.Cancelled"/> ⇒ <c>CancelledDate IS NOT NULL</c> AND <c>CancelReason IS NOT NULL</c>.</item>
///   <item><see cref="ClaimStatus.PartiallyPaid"/> ⇒ <c>PaidAmount &gt; 0</c> AND <c>PaidAmount &lt; PrincipalAmount</c>.</item>
/// </list>
/// </summary>
public sealed class ClaimStatusCoherenceCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "CLAIM.STATUS_INCOHERENT";

    /// <inheritdoc />
    public string AggregateName => nameof(Claim);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.Medium;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var claims = await ctx.Db.Claims
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var claim in claims)
        {
            switch (claim.Status)
            {
                case ClaimStatus.Settled:
                    if (claim.RemainingAmount != 0m || claim.SettledDate is null)
                    {
                        findings.Add(BuildFinding(
                            claim,
                            $"Settled claim must have RemainingAmount=0 and SettledDate set; observed Remaining={claim.RemainingAmount}, SettledDate={(claim.SettledDate?.ToString("O") ?? "null")}.",
                            "RemainingAmount=0; SettledDate!=null",
                            $"RemainingAmount={claim.RemainingAmount}; SettledDate={(claim.SettledDate?.ToString("O") ?? "null")}"));
                    }
                    break;

                case ClaimStatus.Cancelled:
                    if (claim.CancelledDate is null || string.IsNullOrWhiteSpace(claim.CancelReason))
                    {
                        findings.Add(BuildFinding(
                            claim,
                            "Cancelled claim must have CancelledDate AND CancelReason populated.",
                            "CancelledDate!=null; CancelReason!=null",
                            $"CancelledDate={(claim.CancelledDate?.ToString("O") ?? "null")}; CancelReason={(claim.CancelReason is null ? "null" : "set")}"));
                    }
                    break;

                case ClaimStatus.PartiallyPaid:
                    if (claim.PaidAmount <= 0m || claim.PaidAmount >= claim.PrincipalAmount)
                    {
                        findings.Add(BuildFinding(
                            claim,
                            "PartiallyPaid claim must satisfy 0 < PaidAmount < PrincipalAmount.",
                            $"0 < PaidAmount < {claim.PrincipalAmount}",
                            $"PaidAmount={claim.PaidAmount}; PrincipalAmount={claim.PrincipalAmount}"));
                    }
                    break;
            }
        }

        return new IntegrityCheckPartialResult(claims.Count, findings);
    }

    /// <summary>Helper that constructs a finding record for a Claim row.</summary>
    /// <param name="claim">Offending claim row.</param>
    /// <param name="description">Human-readable description (PII-free).</param>
    /// <param name="expected">Expected invariant.</param>
    /// <param name="actual">Observed state.</param>
    /// <returns>The finding record.</returns>
    private IntegrityCheckFindingRecord BuildFinding(Claim claim, string description, string expected, string actual)
        => new(
            CheckCode: CheckCode,
            Severity: Severity,
            AggregateName: AggregateName,
            AggregateRowId: claim.Id,
            Description: description,
            ExpectedValue: expected,
            ActualValue: actual);
}
