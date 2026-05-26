using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for <see cref="TreasuryFeedImporter"/>.
/// </summary>
public sealed class TreasuryFeedImporterTests
{
    /// <summary>Happy path inserts new TreasuryPaymentReceipt rows + counters.</summary>
    [Fact]
    public async Task ImportAsync_HappyPath_InsertsReceiptsAndCounters()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        var payerId = await TreasuryFeedTestHelpers.SeedContributorAsync(db, "1000000000003");

        var src = new InMemoryTreasuryFeedSource();
        var csv = TreasuryFeedTestHelpers.BuildCsv(
            ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref-1"),
            ("TR-002", "2026-05-22", "1000000000003", "Test Payer", "50.00", "MD12", "ref-2"));
        src.Seed(TreasuryFeedTestHelpers.FeedDate, csv);

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out var codes);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);

        var result = await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Scheduled);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(TreasuryFeedImportStatus.Completed.ToString());
        result.Value.RowsTotal.Should().Be(2);
        result.Value.RowsImported.Should().Be(2);
        result.Value.RowsFailed.Should().Be(0);

        var receipts = await db.TreasuryPaymentReceipts.ToListAsync();
        receipts.Should().HaveCount(2);
        receipts.Should().AllSatisfy(r => r.PayerContributorId.Should().Be(payerId));

        codes.Should().Contain(ITreasuryFeedImporter.AuditImportCompleted);
    }

    /// <summary>Second import of an identical feed records all rows as Skipped.</summary>
    [Fact]
    public async Task ImportAsync_SecondIdenticalImport_RecordsAllRowsSkipped()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        await TreasuryFeedTestHelpers.SeedContributorAsync(db, "1000000000003");

        var src = new InMemoryTreasuryFeedSource();
        var csv = TreasuryFeedTestHelpers.BuildCsv(
            ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref-1"));
        src.Seed(TreasuryFeedTestHelpers.FeedDate, csv);

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out _);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);

        await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Scheduled);
        var second = await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Scheduled);

        second.IsSuccess.Should().BeTrue();
        second.Value.RowsImported.Should().Be(0);
        second.Value.RowsSkipped.Should().Be(1);

        // Only one TreasuryPaymentReceipt exists after both runs.
        var receipts = await db.TreasuryPaymentReceipts.ToListAsync();
        receipts.Should().ContainSingle();
    }

    /// <summary>A re-imported row with a different amount updates the existing receipt.</summary>
    [Fact]
    public async Task ImportAsync_AmountDrift_UpdatesExistingReceipt()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        await TreasuryFeedTestHelpers.SeedContributorAsync(db, "1000000000003");

        var src = new InMemoryTreasuryFeedSource();
        src.Seed(TreasuryFeedTestHelpers.FeedDate,
            TreasuryFeedTestHelpers.BuildCsv(
                ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref-1")));

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out _);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);
        await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Scheduled);

        // Reseed the same date with a drifted amount + retry the import.
        src.Seed(TreasuryFeedTestHelpers.FeedDate,
            TreasuryFeedTestHelpers.BuildCsv(
                ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "150.00", "MD12", "ref-1")));
        var second = await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Scheduled);

        second.IsSuccess.Should().BeTrue();
        second.Value.RowsUpdated.Should().Be(1);
        second.Value.RowsImported.Should().Be(0);

        var receipt = await db.TreasuryPaymentReceipts.SingleAsync();
        receipt.AmountReceived.Should().Be(150.00m);
    }

    /// <summary>NotFound from the source flips the import to Failed with a sanitised reason.</summary>
    [Fact]
    public async Task ImportAsync_SourceNotFound_RecordsFailedStatus()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        var src = new InMemoryTreasuryFeedSource();
        // Intentionally do NOT seed FeedDate.

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out var codes);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);

        var result = await importer.ImportAsync(TreasuryFeedTestHelpers.FeedDate, TreasuryFeedTriggerKind.Manual);

        result.IsFailure.Should().BeTrue();
        var import = await db.TreasuryFeedImports.SingleAsync();
        import.Status.Should().Be(TreasuryFeedImportStatus.Failed);
        import.FailureReason.Should().NotBeNullOrEmpty();
        codes.Should().Contain(ITreasuryFeedImporter.AuditImportFailed);
    }
}
