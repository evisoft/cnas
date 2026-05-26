using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ExecutoryDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.ExecutoryDocuments;

/// <summary>
/// R1406 / TOR §3.6-G — tests for the per-payment withholding calculator.
/// Covers each <see cref="ExecutoryDocumentWithholdingMode"/> branch, the
/// priority ordering rule, and the 70% cap enforcement.
/// </summary>
public sealed class ExecutoryDocumentWithholdingCalculatorTests
{
    /// <summary>Fixed period the documents cover.</summary>
    private static readonly DateOnly BenefitPeriod = new(2026, 6, 1);

    /// <summary>Canonical Moldovan IBAN reused by the seed rows.</summary>
    private const string Iban = "MD24AG000225100013104168";

    /// <summary>Canonical IDNP reused by the seed rows.</summary>
    private const string Idnp = "2002000000007";

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-exec-calc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub that round-trips "EXE-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"EXE-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("EXE-", StringComparison.Ordinal)
                && long.TryParse(s["EXE-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Seeds an Active executory-document row using the test deterministic-hasher.</summary>
    private static async Task<ExecutoryDocument> SeedDocumentAsync(
        CnasDbContext db,
        ExecutoryDocumentWithholdingMode mode,
        int priorityRank,
        decimal? amount = null,
        decimal? percentage = null,
        decimal? totalOwed = null,
        decimal totalWithheld = 0m,
        ExecutoryDocumentStatus status = ExecutoryDocumentStatus.Active,
        DateOnly? effectiveFrom = null,
        DateOnly? effectiveUntil = null,
        int seq = 0)
    {
        var doc = new ExecutoryDocument
        {
            DocumentSeriesNumber = $"EXE-2026-{seq:D6}",
            DebtorIdnp = Idnp,
            DebtorIdnpHash = IdHashHelper.Hash(Idnp),
            Kind = ExecutoryDocumentKind.CourtOrder,
            Status = status,
            IssuedBy = "Judecătoria",
            IssuedDate = new DateOnly(2026, 5, 1),
            EffectiveFrom = effectiveFrom ?? new DateOnly(2026, 5, 15),
            EffectiveUntil = effectiveUntil,
            WithholdingMode = mode,
            WithholdingAmountMdl = amount,
            WithholdingPercentage = percentage,
            PriorityRank = priorityRank,
            CreditorAccountIban = Iban,
            CreditorAccountIbanHash = IdHashHelper.Hash(Iban),
            CreditorName = "Creditor",
            TotalOwedMdl = totalOwed,
            TotalWithheldMdl = totalWithheld,
            RegisteredByUserId = 1,
            CreatedAtUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        };
        db.ExecutoryDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>R1406 — FixedAmount single doc — full allocation.</summary>
    [Fact]
    public async Task Calculate_FixedAmount_SingleDoc()
    {
        var db = CreateContext();
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.FixedAmount, priorityRank: 1, amount: 1_500m, totalOwed: 10_000m, seq: 1);
        var sut = new ExecutoryDocumentWithholdingCalculator(db, NewSqidMock(), IdHashHelper.Instance);

        var plan = await sut.CalculateWithholdingsAsync(Idnp, grossBenefitMdl: 5_000m, legalMinimumMdl: 2_000m, benefitPeriod: BenefitPeriod);

        plan.IsSuccess.Should().BeTrue();
        plan.Value.Rows.Should().HaveCount(1);
        plan.Value.Rows[0].AllocatedMdl.Should().Be(1_500m);
        plan.Value.Rows[0].Rationale.Should().Be(ExecutoryDocumentWithholdingCalculator.RationaleFull);
        plan.Value.TotalWithheldMdl.Should().Be(1_500m);
        plan.Value.NetPayableMdl.Should().Be(3_500m);
        plan.Value.CapHit.Should().BeFalse();
    }

    /// <summary>R1406 — Percentage single doc — full allocation.</summary>
    [Fact]
    public async Task Calculate_Percentage_SingleDoc()
    {
        var db = CreateContext();
        // 20% of 5000 = 1000. Limit by TotalOwed = 10_000 (no clip).
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.Percentage, priorityRank: 1, percentage: 20m, totalOwed: 10_000m, seq: 1);
        var sut = new ExecutoryDocumentWithholdingCalculator(db, NewSqidMock(), IdHashHelper.Instance);

        var plan = await sut.CalculateWithholdingsAsync(Idnp, grossBenefitMdl: 5_000m, legalMinimumMdl: 2_000m, benefitPeriod: BenefitPeriod);

        plan.Value.Rows[0].AllocatedMdl.Should().Be(1_000m);
        plan.Value.Rows[0].Rationale.Should().Be(ExecutoryDocumentWithholdingCalculator.RationaleFull);
    }

    /// <summary>R1406 — FullExcessOverMinimum withholds gross above minimum.</summary>
    [Fact]
    public async Task Calculate_FullExcessOverMinimum_SubtractsFloor()
    {
        var db = CreateContext();
        // Excess = 5000 - 2000 = 3000. But the cap is 0.7 * 5000 = 3500 so all 3000 is allocated.
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.FullExcessOverMinimum, priorityRank: 1, totalOwed: 100_000m, seq: 1);
        var sut = new ExecutoryDocumentWithholdingCalculator(db, NewSqidMock(), IdHashHelper.Instance);

        var plan = await sut.CalculateWithholdingsAsync(Idnp, grossBenefitMdl: 5_000m, legalMinimumMdl: 2_000m, benefitPeriod: BenefitPeriod);

        plan.Value.Rows[0].AllocatedMdl.Should().Be(3_000m);
        plan.Value.Rows[0].Rationale.Should().Be(ExecutoryDocumentWithholdingCalculator.RationaleFull);
        plan.Value.NetPayableMdl.Should().Be(2_000m);
    }

    /// <summary>R1406 — priority ordering: when two docs exceed 70% cap the lower-priority one gets clipped.</summary>
    [Fact]
    public async Task Calculate_TwoDocsExceedCap_LowerPriorityClipped()
    {
        var db = CreateContext();
        // gross = 5000, cap = 3500.
        // doc1 (priority=1): 50% → 2500. Allocated fully.
        // doc2 (priority=2): 40% → 2000. Residual cap = 1000 → partial allocation.
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.Percentage, priorityRank: 1, percentage: 50m, totalOwed: 100_000m, seq: 1);
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.Percentage, priorityRank: 2, percentage: 40m, totalOwed: 100_000m, seq: 2);
        var sut = new ExecutoryDocumentWithholdingCalculator(db, NewSqidMock(), IdHashHelper.Instance);

        var plan = await sut.CalculateWithholdingsAsync(Idnp, grossBenefitMdl: 5_000m, legalMinimumMdl: 0m, benefitPeriod: BenefitPeriod);

        plan.Value.Rows.Should().HaveCount(2);
        plan.Value.Rows[0].AllocatedMdl.Should().Be(2_500m);
        plan.Value.Rows[0].Rationale.Should().Be(ExecutoryDocumentWithholdingCalculator.RationaleFull);
        plan.Value.Rows[1].AllocatedMdl.Should().Be(1_000m);
        plan.Value.Rows[1].Rationale.Should().Be(ExecutoryDocumentWithholdingCalculator.RationalePartial);
        plan.Value.CapHit.Should().BeTrue();
        plan.Value.TotalWithheldMdl.Should().Be(3_500m);
    }

    /// <summary>R1406 — Inactive documents (Status != Active) are filtered out.</summary>
    [Fact]
    public async Task Calculate_InactiveDocs_FilteredOut()
    {
        var db = CreateContext();
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.FixedAmount, priorityRank: 1, amount: 500m, totalOwed: 1_000m, status: ExecutoryDocumentStatus.Suspended, seq: 1);
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.FixedAmount, priorityRank: 2, amount: 700m, totalOwed: 1_000m, status: ExecutoryDocumentStatus.Cancelled, seq: 2);
        await SeedDocumentAsync(db, ExecutoryDocumentWithholdingMode.FixedAmount, priorityRank: 3, amount: 200m, totalOwed: 1_000m, status: ExecutoryDocumentStatus.Active, seq: 3);
        var sut = new ExecutoryDocumentWithholdingCalculator(db, NewSqidMock(), IdHashHelper.Instance);

        var plan = await sut.CalculateWithholdingsAsync(Idnp, grossBenefitMdl: 5_000m, legalMinimumMdl: 0m, benefitPeriod: BenefitPeriod);

        plan.Value.Rows.Should().HaveCount(1);
        plan.Value.Rows[0].AllocatedMdl.Should().Be(200m);
    }
}
