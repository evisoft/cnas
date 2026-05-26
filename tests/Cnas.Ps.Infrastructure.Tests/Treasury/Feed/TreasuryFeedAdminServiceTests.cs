using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Treasury.Feed;

namespace Cnas.Ps.Infrastructure.Tests.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for <see cref="TreasuryFeedAdminService"/>.
/// </summary>
public sealed class TreasuryFeedAdminServiceTests
{
    /// <summary>Manual import defers to the importer and emits the manual-start audit.</summary>
    [Fact]
    public async Task TriggerManualImportAsync_HappyPath_InvokesImporter()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();
        await TreasuryFeedTestHelpers.SeedContributorAsync(db, "1000000000003");
        var src = new InMemoryTreasuryFeedSource();
        src.Seed(TreasuryFeedTestHelpers.FeedDate,
            TreasuryFeedTestHelpers.BuildCsv(
                ("TR-001", "2026-05-22", "1000000000003", "Test Payer", "100.00", "MD12", "ref")));

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out var codes);
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);
        var svc = TreasuryFeedTestHelpers.NewAdminService(db, audit, importer);

        var result = await svc.TriggerManualImportAsync(TreasuryFeedTestHelpers.FeedDate);

        result.IsSuccess.Should().BeTrue();
        result.Value.TriggerKind.Should().Be(TreasuryFeedTriggerKind.Manual.ToString());
        codes.Should().Contain(ITreasuryFeedAdminService.AuditManualImportStarted);
    }

    /// <summary>List filtered by status returns only matching rows.</summary>
    [Fact]
    public async Task ListAsync_FilterByStatus_ReturnsOnlyMatchingRows()
    {
        using var db = TreasuryFeedTestHelpers.CreateContext();

        // Seed two imports manually — one Completed, one Failed.
        db.TreasuryFeedImports.AddRange(
            new TreasuryFeedImport
            {
                FeedDate = new DateOnly(2026, 5, 20),
                Status = TreasuryFeedImportStatus.Completed,
                SourceKind = TreasuryFeedSourceKind.InMemoryTest,
                TriggerKind = TreasuryFeedTriggerKind.Scheduled,
                StartedAt = TreasuryFeedTestHelpers.ClockNow.AddDays(-3),
                CompletedAt = TreasuryFeedTestHelpers.ClockNow.AddDays(-3).AddMinutes(1),
                CreatedAtUtc = TreasuryFeedTestHelpers.ClockNow.AddDays(-3),
                IsActive = true,
            },
            new TreasuryFeedImport
            {
                FeedDate = new DateOnly(2026, 5, 21),
                Status = TreasuryFeedImportStatus.Failed,
                SourceKind = TreasuryFeedSourceKind.InMemoryTest,
                TriggerKind = TreasuryFeedTriggerKind.Manual,
                StartedAt = TreasuryFeedTestHelpers.ClockNow.AddDays(-2),
                FailureReason = "NotFound: not seeded",
                CreatedAtUtc = TreasuryFeedTestHelpers.ClockNow.AddDays(-2),
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var audit = TreasuryFeedTestHelpers.NewAuditCapturing(out _);
        var src = new InMemoryTreasuryFeedSource();
        var importer = TreasuryFeedTestHelpers.NewImporter(db, src, audit);
        var svc = TreasuryFeedTestHelpers.NewAdminService(db, audit, importer);

        var page = await svc.ListAsync(new TreasuryFeedImportFilterDto(Status: "Completed"));

        page.IsSuccess.Should().BeTrue();
        page.Value.Items.Should().ContainSingle()
            .Which.Status.Should().Be(TreasuryFeedImportStatus.Completed.ToString());
    }
}
