using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Registers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Registers;

/// <summary>
/// R1601 / TOR Annex 3.9 — pins the contract of the decisions-register
/// projection over <see cref="Document"/> rows of kind <see cref="DocumentKind.Decision"/>.
/// </summary>
public sealed class DecisionsRegisterTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-reg-dec-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private static (DecisionsRegister Sut, CnasDbContext Db) Create()
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return (new DecisionsRegister(db, sqids), db);
    }

    private static Document SeedDecision(CnasDbContext db, string title, DateTime issuedAtUtc, bool isActive = true)
    {
        var doc = new Document
        {
            CreatedAtUtc = issuedAtUtc,
            Kind = DocumentKind.Decision,
            Title = title,
            MimeType = "application/octet-stream",
            SizeBytes = 0,
            StorageObjectKey = $"k/{Guid.NewGuid():N}",
            StorageBucket = "cnas-documents",
            ContentSha256Hex = string.Empty,
            IsSigned = false,
            IsActive = isActive,
        };
        db.Documents.Add(doc);
        db.SaveChanges();
        return doc;
    }

    /// <summary>Happy path — only Decision-kind active rows are returned.</summary>
    [Fact]
    public async Task ListAsync_DefaultFilter_ReturnsActiveDecisionRows()
    {
        var (sut, db) = Create();
        SeedDecision(db, "DECIZIE_PENSIE row", ClockNow.AddDays(-1));
        SeedDecision(db, "DECIZIE_RECUPERARE_SUME row", ClockNow);

        // Seed a non-decision document — must not appear.
        db.Documents.Add(new Document
        {
            CreatedAtUtc = ClockNow,
            Kind = DocumentKind.Attachment,
            Title = "noise",
            MimeType = "x/y",
            SizeBytes = 0,
            StorageObjectKey = "k",
            StorageBucket = "b",
            ContentSha256Hex = "",
            IsSigned = false,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await sut.ListAsync(new DecisionRegisterFilter(), page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().HaveCount(2);
    }

    /// <summary>Filter — DecisionTypeCode narrows results.</summary>
    [Fact]
    public async Task ListAsync_TypeCodeFilter_NarrowsResults()
    {
        var (sut, db) = Create();
        SeedDecision(db, "DECIZIE_PENSIE happy", ClockNow.AddDays(-1));
        SeedDecision(db, "DECIZIE_RECUPERARE_SUME match", ClockNow);

        var result = await sut.ListAsync(
            new DecisionRegisterFilter(DecisionTypeCode: "DECIZIE_RECUPERARE_SUME"),
            page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title().Should().Contain("RECUPERARE");
    }

    /// <summary>Ordering — newest decision first by IssuedAtUtc DESC.</summary>
    [Fact]
    public async Task ListAsync_OrdersByIssuedAtUtcDesc()
    {
        var (sut, db) = Create();
        var older = SeedDecision(db, "older", ClockNow.AddDays(-2));
        var newer = SeedDecision(db, "newer", ClockNow);

        var result = await sut.ListAsync(new DecisionRegisterFilter(), page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].Sqid.Should().Be($"SQID-{newer.Id}");
        result.Value.Items[1].Sqid.Should().Be($"SQID-{older.Id}");
    }

    /// <summary>Invalid window (from after to) is rejected.</summary>
    [Fact]
    public async Task ListAsync_InvalidWindow_ReturnsValidationFailed()
    {
        var (sut, _) = Create();

        var result = await sut.ListAsync(
            new DecisionRegisterFilter(FromUtc: ClockNow, ToUtc: ClockNow.AddDays(-1)),
            page: 1, pageSize: 20);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}

/// <summary>Small helper expressions used in tests.</summary>
internal static class DecisionsRegisterTestExtensions
{
    /// <summary>Echoes the row's Decision type code into a "Title" lookup so tests can assert against it.</summary>
    public static string Title(this DecisionRegisterRowDto row) =>
        row.DecisionTypeCode + " " + row.DecisionNumber;
}
