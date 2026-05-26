using System;
using System.Collections.Generic;
using System.Linq;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2279 / TOR SEC 033 — tests for
/// <see cref="ClassificationCatalogAdminController"/>. Verifies the cnas-admin
/// authorize gate, the snapshot capture happy path, the filtered details path,
/// and the acknowledgement endpoint.
/// </summary>
public sealed class ClassificationCatalogAdminControllerTests
{
    private static ClassificationCatalogSnapshotDto NewSnapshotDto(string id = "SQID-1")
        => new(
            Id: id,
            CapturedAt: new DateTime(2026, 5, 23, 3, 30, 0, DateTimeKind.Utc),
            TriggerKind: "Manual",
            Status: "Captured",
            TotalTypesScanned: 5,
            TotalPropertiesClassified: 8,
            TotalPropertiesUnclassified: 2,
            LabelCounts: new Dictionary<string, int> { ["Internal"] = 8 },
            AssemblyVersions: new Dictionary<string, string> { ["Cnas.Ps.Contracts"] = "1.0.0.0" },
            FailureReason: null);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(ClassificationCatalogAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task CaptureSnapshot_HappyPath_Returns200()
    {
        var dto = NewSnapshotDto();
        var svc = Substitute.For<IClassificationCatalogService>();
        svc.CaptureManualSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationCatalogSnapshotDto>.Success(dto)));
        var controller = new ClassificationCatalogAdminController(svc);

        var result = await controller.CaptureSnapshotAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetSnapshot_HappyPath_Returns200()
    {
        var dto = NewSnapshotDto("SQID-2");
        var svc = Substitute.For<IClassificationCatalogService>();
        svc.GetSnapshotByIdAsync("SQID-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationCatalogSnapshotDto>.Success(dto)));
        var controller = new ClassificationCatalogAdminController(svc);

        var result = await controller.GetSnapshotAsync("SQID-2");

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetSnapshotDetails_PassesFilterToService()
    {
        var snapshot = NewSnapshotDto("SQID-3");
        var details = new ClassificationCatalogSnapshotDetailsDto(
            Snapshot: snapshot,
            Entries: Array.Empty<ClassificationCatalogEntryDto>(),
            Total: 0,
            Skip: 0,
            Take: 50);
        var svc = Substitute.For<IClassificationCatalogService>();
        svc.GetSnapshotDetailsAsync("SQID-3", Arg.Is<ClassificationCatalogEntryFilterDto>(f => f.Label == "Public"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationCatalogSnapshotDetailsDto>.Success(details)));
        var controller = new ClassificationCatalogAdminController(svc);

        var result = await controller.GetSnapshotDetailsAsync(
            "SQID-3",
            label: "Public",
            isExplicit: null,
            typeFullNameContains: null,
            skip: 0,
            take: 50);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(details);
    }

    [Fact]
    public async Task Acknowledge_HappyPath_Returns200()
    {
        var dto = new ClassificationDriftFindingDto(
            Id: "SQID-F1",
            BaselineSnapshotSqid: "SQID-1",
            CurrentSnapshotSqid: "SQID-2",
            DriftKind: "LabelChanged",
            TypeFullName: "Cnas.Ps.Contracts.SampleDto",
            PropertyName: "Code",
            BaselineLabel: "Public",
            CurrentLabel: "Internal",
            Acknowledged: true,
            AcknowledgedAt: DateTime.UtcNow,
            AcknowledgementNote: "Reviewed and approved.",
            DetectedAt: DateTime.UtcNow);
        var input = new ClassificationDriftAcknowledgeInputDto("Reviewed and approved.");
        var svc = Substitute.For<IClassificationCatalogService>();
        svc.AcknowledgeDriftAsync("SQID-F1", input, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationDriftFindingDto>.Success(dto)));
        var controller = new ClassificationCatalogAdminController(svc);

        var result = await controller.AcknowledgeDriftAsync("SQID-F1", input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetSnapshot_NotFound_Returns404()
    {
        var svc = Substitute.For<IClassificationCatalogService>();
        svc.GetSnapshotByIdAsync("SQID-999", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ClassificationCatalogSnapshotDto>.Failure(ErrorCodes.NotFound, "missing")));
        var controller = new ClassificationCatalogAdminController(svc);

        var result = await controller.GetSnapshotAsync("SQID-999");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
