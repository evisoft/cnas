using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1810 / TOR BP 1.2-I — tests for <see cref="TreasuryFeedAdminController"/>.
/// Verifies the cnas-admin authorize gate, the manual-import happy path, the
/// per-import lookup, and the imports list endpoint.
/// </summary>
public sealed class TreasuryFeedAdminControllerTests
{
    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(TreasuryFeedAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task TriggerManualImport_HappyPath_Returns200()
    {
        var summary = new TreasuryFeedImportSummaryDto(
            Id: "SQID-1",
            FeedDate: new DateOnly(2026, 5, 22),
            Status: TreasuryFeedImportStatus.Completed.ToString(),
            RowsTotal: 1,
            RowsImported: 1,
            RowsUpdated: 0,
            RowsSkipped: 0,
            RowsFailed: 0,
            TriggerKind: TreasuryFeedTriggerKind.Manual.ToString());
        var svc = Substitute.For<ITreasuryFeedAdminService>();
        svc.TriggerManualImportAsync(new DateOnly(2026, 5, 22), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TreasuryFeedImportSummaryDto>.Success(summary)));

        var controller = new TreasuryFeedAdminController(svc);
        var result = await controller.TriggerManualImportAsync("2026-05-22", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(summary);
    }

    [Fact]
    public async Task GetImport_HappyPath_Returns200()
    {
        var dto = new TreasuryFeedImportDto(
            Id: "SQID-1",
            FeedDate: new DateOnly(2026, 5, 22),
            Status: TreasuryFeedImportStatus.Completed.ToString(),
            SourceKind: TreasuryFeedSourceKind.InMemoryTest.ToString(),
            SourceReference: "in-memory-fixture:2026-05-22",
            FileSizeBytes: 100,
            FileHashSha256: new string('a', 64),
            RowsTotal: 1, RowsImported: 1, RowsUpdated: 0, RowsSkipped: 0, RowsFailed: 0,
            StartedAt: DateTime.UtcNow,
            CompletedAt: DateTime.UtcNow,
            FailureReason: null,
            TriggerKind: TreasuryFeedTriggerKind.Scheduled.ToString());
        var svc = Substitute.For<ITreasuryFeedAdminService>();
        svc.GetImportByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TreasuryFeedImportDto>.Success(dto)));

        var controller = new TreasuryFeedAdminController(svc);
        var result = await controller.GetImportAsync("SQID-1");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task List_FilteredByStatus_Returns200()
    {
        var page = new TreasuryFeedImportPageDto(
            Items: Array.Empty<TreasuryFeedImportDto>(),
            Total: 0, Skip: 0, Take: 50);
        var svc = Substitute.For<ITreasuryFeedAdminService>();
        svc.ListAsync(Arg.Is<TreasuryFeedImportFilterDto>(f => f.Status == "Completed"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TreasuryFeedImportPageDto>.Success(page)));

        var controller = new TreasuryFeedAdminController(svc);
        var result = await controller.ListAsync(status: "Completed");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(page);
    }
}
