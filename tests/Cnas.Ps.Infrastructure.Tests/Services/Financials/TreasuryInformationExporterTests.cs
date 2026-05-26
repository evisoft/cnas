using System.Text;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Financials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Financials;

/// <summary>
/// R0816 / TOR BP 1.2-G — tests for <see cref="TreasuryInformationExporter"/>.
/// Verifies the slice predicates (approved/issued refunds + open/partial
/// claims in the rolling 30-day window) and the format dispatch.
/// </summary>
public sealed class TreasuryInformationExporterTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Today's date in the test clock's UTC reference.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-treasury-info-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid stub that prefixes every encoded id with "SQID-".</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Builds the system under test wired against the supplied context.</summary>
    private static TreasuryInformationExporter NewExporter(CnasDbContext db)
        => new(db, new StubClock(ClockNow), NewSqidStub());

    /// <summary>Seeds a refund row in the supplied lifecycle state with the supplied issuedDate.</summary>
    private static async Task<long> SeedRefundAsync(
        CnasDbContext db,
        BassRefundStatus status,
        decimal amount,
        DateOnly? issuedDate = null,
        bool isActive = true)
    {
        var entity = new BassRefund
        {
            ContributorId = 1L,
            RelatedMonth = new DateOnly(2026, 4, 1),
            RefundAmount = amount,
            Status = status,
            RequestedByUserId = 1L,
            IssuedDate = issuedDate,
            CreatedAtUtc = ClockNow.AddDays(-3),
            IsActive = isActive,
        };
        db.BassRefunds.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    /// <summary>Seeds a claim row scoped to the rolling 30-day window.</summary>
    private static async Task<long> SeedClaimAsync(
        CnasDbContext db,
        ClaimStatus status,
        decimal remaining,
        DateOnly? openedDate = null)
    {
        var entity = new Claim
        {
            ContributorId = 2L,
            ClaimNumber = $"CRN-2026-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            RelatedMonth = new DateOnly(2026, 4, 1),
            Kind = ClaimKind.Contribution,
            PrincipalAmount = remaining,
            PaidAmount = 0m,
            RemainingAmount = remaining,
            Status = status,
            OpenedDate = openedDate ?? Today.AddDays(-3),
            CreatedAtUtc = ClockNow.AddDays(-3),
            IsActive = true,
        };
        db.Claims.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    /// <summary>R0816 — refunds in Approved/Issued without IssuedDate aggregate into the XML payload.</summary>
    [Fact]
    public async Task GenerateAsync_AggregatesApprovedRefundsAndOutstandingClaims()
    {
        using var db = CreateContext();
        await SeedRefundAsync(db, BassRefundStatus.Approved, amount: 250m);
        await SeedRefundAsync(db, BassRefundStatus.IssuedToTreasury, amount: 100m, issuedDate: null);
        await SeedClaimAsync(db, ClaimStatus.Open, remaining: 500m);
        await SeedClaimAsync(db, ClaimStatus.PartiallyPaid, remaining: 200m);
        var sut = NewExporter(db);

        var result = await sut.GenerateAsync(Today, TreasuryInformationExporter.FormatXml);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundCount.Should().Be(2);
        result.Value.OutstandingClaimCount.Should().Be(2);
        result.Value.TotalRefundAmount.Should().Be(350m);
        result.Value.TotalOutstandingAmount.Should().Be(700m);
        result.Value.Format.Should().Be("XML");
        result.Value.FileName.Should().EndWith(".xml");
        // Smoke-test that the XML parses cleanly.
        var xml = XDocument.Parse(Encoding.UTF8.GetString(result.Value.Content));
        xml.Root!.Name.LocalName.Should().Be("TreasuryInformation");
    }

    /// <summary>R0816 — Cancelled refunds are excluded from the payload.</summary>
    [Fact]
    public async Task GenerateAsync_ExcludesCancelledRefunds()
    {
        using var db = CreateContext();
        await SeedRefundAsync(db, BassRefundStatus.Cancelled, amount: 99m);
        await SeedRefundAsync(db, BassRefundStatus.Approved, amount: 250m);
        var sut = NewExporter(db);

        var result = await sut.GenerateAsync(Today, TreasuryInformationExporter.FormatXml);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundCount.Should().Be(1);
        result.Value.TotalRefundAmount.Should().Be(250m);
    }

    /// <summary>R0816 — a future operating date is rejected with ValidationFailed.</summary>
    [Fact]
    public async Task GenerateAsync_FutureForDate_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var sut = NewExporter(db);

        var result = await sut.GenerateAsync(Today.AddDays(5), TreasuryInformationExporter.FormatXml);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Be(TreasuryInformationExporter.ForDateInFutureMessage);
    }

    /// <summary>R0816 — CSV format dispatch produces a non-empty payload with the CSV filename suffix.</summary>
    [Fact]
    public async Task GenerateAsync_CsvFormat_EmitsCsvPayload()
    {
        using var db = CreateContext();
        await SeedRefundAsync(db, BassRefundStatus.Approved, amount: 50m);
        var sut = NewExporter(db);

        var result = await sut.GenerateAsync(Today, "csv");

        result.IsSuccess.Should().BeTrue();
        result.Value.Format.Should().Be("CSV");
        result.Value.FileName.Should().EndWith(".csv");
        var csv = Encoding.UTF8.GetString(result.Value.Content);
        csv.Should().Contain("# Refunds");
        csv.Should().Contain("SQID-1");
    }
}
