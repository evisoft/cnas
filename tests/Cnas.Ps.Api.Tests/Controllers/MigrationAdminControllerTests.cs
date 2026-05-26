using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2430 / R2431 / R2433 / TOR M4 — tests for <see cref="MigrationAdminController"/>.
/// </summary>
public sealed class MigrationAdminControllerTests
{
    private static MigrationRunSummaryDto NewSummaryDto()
        => new(
            Id: "SQID-RUN-1",
            PlanSqid: "SQID-1",
            Status: "Completed",
            TriggerKind: "Manual",
            TotalSourceRowsSeen: 5,
            TotalRowsImported: 5,
            TotalRowsUpdated: 0,
            TotalRowsSkipped: 0,
            TotalRowsFailed: 0,
            IsDryRun: false);

    private static ReconciliationReportDto NewReconciliationDto()
        => new(
            Id: "SQID-REC-1",
            RunSqid: "SQID-RUN-1",
            Status: "Passed",
            SourceRowCount: 5,
            TargetRowCount: 5,
            MissingInTargetCount: 0,
            UnexpectedInTargetCount: 0,
            ChecksumMatchRate: 1.0000m,
            DiscrepancyDetailsJson: null,
            ComputedAt: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc));

    private static MigrationFindingDto NewFindingDto()
        => new(
            Id: "SQID-F-1",
            RunSqid: "SQID-RUN-1",
            BatchOrdinal: 1,
            RowOrdinalInBatch: 0,
            Severity: "Info",
            FindingCode: "MAPPING.UNCUSTOMISED",
            Description: "passthrough",
            SourceFingerprint: "fp-1",
            Acknowledged: true,
            AcknowledgedAt: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc),
            AcknowledgedByUserSqid: "USR-1",
            AcknowledgementNote: "ok.");

    [Fact]
    public async Task TriggerManualImport_HappyPath_Returns200()
    {
        var summary = NewSummaryDto();
        var admin = Substitute.For<IMigrationAdminService>();
        admin.TriggerManualImportAsync("SQID-1", true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MigrationRunSummaryDto>.Success(summary)));
        var reconciler = Substitute.For<IMigrationReconciler>();
        var controller = new MigrationAdminController(admin, reconciler);

        var result = await controller.TriggerManualImportAsync("SQID-1", dryRun: true, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(summary);
    }

    [Fact]
    public async Task GetReconciliation_HappyPath_Returns200()
    {
        var rec = NewReconciliationDto();
        var admin = Substitute.For<IMigrationAdminService>();
        admin.GetReconciliationAsync("SQID-RUN-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ReconciliationReportDto>.Success(rec)));
        var reconciler = Substitute.For<IMigrationReconciler>();
        var controller = new MigrationAdminController(admin, reconciler);

        var result = await controller.GetReconciliationAsync("SQID-RUN-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rec);
    }

    [Fact]
    public async Task AcknowledgeFinding_HappyPath_Returns200()
    {
        var finding = NewFindingDto();
        var admin = Substitute.For<IMigrationAdminService>();
        admin.AcknowledgeFindingAsync(
                "SQID-F-1",
                Arg.Any<MigrationFindingAcknowledgeInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MigrationFindingDto>.Success(finding)));
        var reconciler = Substitute.For<IMigrationReconciler>();
        var controller = new MigrationAdminController(admin, reconciler);

        var result = await controller.AcknowledgeFindingAsync(
            "SQID-F-1",
            new MigrationFindingAcknowledgeInputDto("Resolved."),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(finding);
    }
}
