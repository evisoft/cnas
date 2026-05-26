using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;

namespace Cnas.Ps.Infrastructure.Tests.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant tests for
/// <see cref="ClaimPaidAmountSumCheck"/>. Verifies the running-total
/// reconciliation between <c>Claim.PaidAmount</c> and the sum of its
/// <c>ClaimPayment</c> children.
/// </summary>
public sealed class ClaimPaidAmountSumCheckTests
{
    [Fact]
    public async Task RunAsync_PaidAmountMatchesSum_ReturnsNoFindings()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var claim = new Claim
        {
            ContributorId = 1,
            ClaimNumber = "CRN-2026-000001",
            RelatedMonth = new DateOnly(2026, 4, 1),
            Kind = ClaimKind.Contribution,
            PrincipalAmount = 1000m,
            PaidAmount = 300m,
            RemainingAmount = 700m,
            Status = ClaimStatus.PartiallyPaid,
            OpenedDate = new DateOnly(2026, 4, 15),
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimPayments.Add(new ClaimPayment
        {
            ClaimId = claim.Id,
            PaidDate = new DateOnly(2026, 5, 1),
            Amount = 200m,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        db.ClaimPayments.Add(new ClaimPayment
        {
            ClaimId = claim.Id,
            PaidDate = new DateOnly(2026, 5, 10),
            Amount = 100m,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        await db.SaveChangesAsync();

        var check = new ClaimPaidAmountSumCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(1);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PaidAmountDivergesFromSum_ProducesFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var claim = new Claim
        {
            ContributorId = 1,
            ClaimNumber = "CRN-2026-000002",
            RelatedMonth = new DateOnly(2026, 4, 1),
            Kind = ClaimKind.Contribution,
            PrincipalAmount = 1000m,
            PaidAmount = 500m, // claim says 500 paid
            RemainingAmount = 500m,
            Status = ClaimStatus.PartiallyPaid,
            OpenedDate = new DateOnly(2026, 4, 15),
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        db.ClaimPayments.Add(new ClaimPayment
        {
            ClaimId = claim.Id,
            PaidDate = new DateOnly(2026, 5, 1),
            Amount = 200m, // children sum to 200, NOT 500
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        await db.SaveChangesAsync();

        var check = new ClaimPaidAmountSumCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(1);
        result.Findings.Should().HaveCount(1);
        var finding = result.Findings[0];
        finding.CheckCode.Should().Be("CLAIM.RUNNING_TOTAL_MISMATCH");
        finding.AggregateRowId.Should().Be(claim.Id);
        finding.Severity.Should().Be(IntegrityFindingSeverity.High);
        finding.ExpectedValue.Should().Be("200.00");
        finding.ActualValue.Should().Be("500.00");
    }
}
