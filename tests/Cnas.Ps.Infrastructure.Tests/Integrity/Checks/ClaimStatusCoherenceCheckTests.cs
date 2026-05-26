using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;

namespace Cnas.Ps.Infrastructure.Tests.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant tests for
/// <see cref="ClaimStatusCoherenceCheck"/>. Verifies the three documented
/// state-machine invariants (Settled / Cancelled / PartiallyPaid).
/// </summary>
public sealed class ClaimStatusCoherenceCheckTests
{
    private static Claim NewClaim(ClaimStatus status, string number = "CRN-2026-000001")
        => new()
        {
            ContributorId = 1,
            ClaimNumber = number,
            RelatedMonth = new DateOnly(2026, 4, 1),
            Kind = ClaimKind.Contribution,
            PrincipalAmount = 1000m,
            PaidAmount = 0m,
            RemainingAmount = 1000m,
            Status = status,
            OpenedDate = new DateOnly(2026, 4, 15),
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };

    [Fact]
    public async Task RunAsync_OpenClaim_NoFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        db.Claims.Add(NewClaim(ClaimStatus.Open));
        await db.SaveChangesAsync();

        var check = new ClaimStatusCoherenceCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SettledButRemainingNonZero_ProducesFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var c = NewClaim(ClaimStatus.Settled);
        c.PaidAmount = 500m;
        c.RemainingAmount = 500m; // INVARIANT VIOLATION — Settled should have Remaining=0
        c.SettledDate = new DateOnly(2026, 5, 1);
        db.Claims.Add(c);
        await db.SaveChangesAsync();

        var check = new ClaimStatusCoherenceCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().HaveCount(1);
        result.Findings[0].CheckCode.Should().Be("CLAIM.STATUS_INCOHERENT");
    }

    [Fact]
    public async Task RunAsync_CancelledWithoutReason_ProducesFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var c = NewClaim(ClaimStatus.Cancelled);
        c.CancelledDate = new DateOnly(2026, 5, 1);
        c.CancelReason = null; // INVARIANT VIOLATION
        db.Claims.Add(c);
        await db.SaveChangesAsync();

        var check = new ClaimStatusCoherenceCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().HaveCount(1);
        result.Findings[0].CheckCode.Should().Be("CLAIM.STATUS_INCOHERENT");
    }

    [Fact]
    public async Task RunAsync_PartiallyPaidWithZeroPaid_ProducesFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var c = NewClaim(ClaimStatus.PartiallyPaid);
        c.PaidAmount = 0m; // INVARIANT VIOLATION — PartiallyPaid requires Paid > 0
        c.RemainingAmount = 1000m;
        db.Claims.Add(c);
        await db.SaveChangesAsync();

        var check = new ClaimStatusCoherenceCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().HaveCount(1);
        result.Findings[0].CheckCode.Should().Be("CLAIM.STATUS_INCOHERENT");
    }
}
