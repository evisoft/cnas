using System;
using System.Collections.Generic;
using System.Linq;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.DataClassification;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Tests.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — service-level tests for
/// <see cref="ClassificationCatalogService"/>. Verifies the manual + scheduled
/// capture lifecycle, the snapshot lookups + filters, the idempotent drift
/// computation, and the acknowledgement audit.
/// </summary>
public sealed class ClassificationCatalogServiceTests
{
    private static IReadOnlyList<ScannedPropertyDto> Sample()
        => new[]
        {
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "Code",
                Label: "Public",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "Name",
                Label: "Internal",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.OtherDto",
                PropertyName: "Email",
                Label: "Confidential",
                IsExplicit: false,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
        };

    [Fact]
    public async Task CaptureManualSnapshot_PersistsSnapshotEntriesAndAudits()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(Sample(), totalTypesScanned: 2);
        var audit = ClassificationCatalogTestHelpers.NewAudit(out var codes);
        var svc = ClassificationCatalogTestHelpers.NewService(db, scanner, audit);

        var result = await svc.CaptureManualSnapshotAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClassificationSnapshotStatus.Captured));
        result.Value.TriggerKind.Should().Be(nameof(ClassificationSnapshotTriggerKind.Manual));
        result.Value.TotalPropertiesClassified.Should().Be(2);
        result.Value.TotalPropertiesUnclassified.Should().Be(1);

        var storedSnapshot = await db.ClassificationCatalogSnapshots.SingleAsync();
        storedSnapshot.Status.Should().Be(ClassificationSnapshotStatus.Captured);
        storedSnapshot.LabelCountsJson.Should().NotBeNullOrEmpty();

        var entries = await db.ClassificationCatalogEntries.ToListAsync();
        entries.Should().HaveCount(3);
        codes.Should().Contain("CLASSIFICATION.SNAPSHOT_CAPTURED");
    }

    [Fact]
    public async Task GetSnapshotById_ReturnsNotFound_ForUnknownSqid()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(Sample());
        var svc = ClassificationCatalogTestHelpers.NewService(db, scanner, ClassificationCatalogTestHelpers.NewAudit(out _));

        var result = await svc.GetSnapshotByIdAsync("SQID-9999");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetSnapshotDetails_FiltersByLabel()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(Sample());
        var svc = ClassificationCatalogTestHelpers.NewService(db, scanner, ClassificationCatalogTestHelpers.NewAudit(out _));
        var captured = await svc.CaptureManualSnapshotAsync();
        captured.IsSuccess.Should().BeTrue();

        var details = await svc.GetSnapshotDetailsAsync(
            captured.Value.Id,
            new ClassificationCatalogEntryFilterDto(
                Label: "Public",
                IsExplicit: null,
                TypeFullNameContains: null,
                Skip: 0,
                Take: 50));

        details.IsSuccess.Should().BeTrue();
        details.Value.Entries.Should().HaveCount(1);
        details.Value.Entries[0].Label.Should().Be("Public");
    }

    [Fact]
    public async Task GetSnapshotDetails_FiltersByTypeFullNameContains()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(Sample());
        var svc = ClassificationCatalogTestHelpers.NewService(db, scanner, ClassificationCatalogTestHelpers.NewAudit(out _));
        var captured = await svc.CaptureManualSnapshotAsync();
        captured.IsSuccess.Should().BeTrue();

        var details = await svc.GetSnapshotDetailsAsync(
            captured.Value.Id,
            new ClassificationCatalogEntryFilterDto(
                Label: null,
                IsExplicit: null,
                TypeFullNameContains: "Other",
                Skip: 0,
                Take: 50));

        details.IsSuccess.Should().BeTrue();
        details.Value.Entries.Should().HaveCount(1);
        details.Value.Entries[0].TypeFullName.Should().Contain("Other");
    }

    [Fact]
    public async Task ComputeDrift_ReturnsAddedFinding_WhenCurrentHasExtraProperty()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        var scanner1 = ClassificationCatalogTestHelpers.NewStubScanner(new[]
        {
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "Code",
                Label: "Public",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
        });
        var audit1 = ClassificationCatalogTestHelpers.NewAudit(out _);
        var svc1 = ClassificationCatalogTestHelpers.NewService(db, scanner1, audit1);
        var baseline = await svc1.CaptureManualSnapshotAsync();
        baseline.IsSuccess.Should().BeTrue();

        var scanner2 = ClassificationCatalogTestHelpers.NewStubScanner(new[]
        {
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "Code",
                Label: "Public",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
            new ScannedPropertyDto(
                TypeFullName: "Cnas.Ps.Contracts.SampleDto",
                PropertyName: "NewField",
                Label: "Internal",
                IsExplicit: true,
                DeclaringAssembly: "Cnas.Ps.Contracts"),
        });
        var audit2 = ClassificationCatalogTestHelpers.NewAudit(out var codes);
        var svc2 = ClassificationCatalogTestHelpers.NewService(db, scanner2, audit2);
        var current = await svc2.CaptureManualSnapshotAsync();
        current.IsSuccess.Should().BeTrue();

        var drift = await svc2.ComputeDriftAsync(baseline.Value.Id, current.Value.Id);

        drift.IsSuccess.Should().BeTrue();
        drift.Value.FindingsCount.Should().Be(1);
        drift.Value.Findings[0].DriftKind.Should().Be(nameof(ClassificationDriftKind.Added));
        drift.Value.Findings[0].PropertyName.Should().Be("NewField");
        codes.Should().Contain("CLASSIFICATION.DRIFT_DETECTED");

        // Idempotent re-run.
        var second = await svc2.ComputeDriftAsync(baseline.Value.Id, current.Value.Id);
        second.IsSuccess.Should().BeTrue();
        second.Value.FindingsCount.Should().Be(1);
    }

    [Fact]
    public async Task AcknowledgeDrift_StampsNoteAndAudits()
    {
        using var db = ClassificationCatalogTestHelpers.CreateContext();
        // Seed an unacknowledged drift finding directly.
        var baselineSnap = new ClassificationCatalogSnapshot
        {
            CapturedAt = ClassificationCatalogTestHelpers.ClockNow,
            TriggerKind = ClassificationSnapshotTriggerKind.Manual,
            Status = ClassificationSnapshotStatus.Captured,
            CreatedAtUtc = ClassificationCatalogTestHelpers.ClockNow,
            IsActive = true,
        };
        var currentSnap = new ClassificationCatalogSnapshot
        {
            CapturedAt = ClassificationCatalogTestHelpers.ClockNow,
            TriggerKind = ClassificationSnapshotTriggerKind.Manual,
            Status = ClassificationSnapshotStatus.Captured,
            CreatedAtUtc = ClassificationCatalogTestHelpers.ClockNow,
            IsActive = true,
        };
        db.ClassificationCatalogSnapshots.Add(baselineSnap);
        db.ClassificationCatalogSnapshots.Add(currentSnap);
        await db.SaveChangesAsync();

        var finding = new ClassificationDriftFinding
        {
            BaselineSnapshotId = baselineSnap.Id,
            CurrentSnapshotId = currentSnap.Id,
            DriftKind = ClassificationDriftKind.LabelChanged,
            TypeFullName = "Cnas.Ps.Contracts.SampleDto",
            PropertyName = "Code",
            BaselineLabel = "Public",
            CurrentLabel = "Internal",
            Acknowledged = false,
            DetectedAt = ClassificationCatalogTestHelpers.ClockNow,
            CreatedAtUtc = ClassificationCatalogTestHelpers.ClockNow,
            IsActive = true,
        };
        db.ClassificationDriftFindings.Add(finding);
        await db.SaveChangesAsync();

        var scanner = ClassificationCatalogTestHelpers.NewStubScanner(Sample());
        var audit = ClassificationCatalogTestHelpers.NewAudit(out var codes);
        var svc = ClassificationCatalogTestHelpers.NewService(db, scanner, audit);

        var ack = await svc.AcknowledgeDriftAsync(
            $"SQID-{finding.Id}",
            new ClassificationDriftAcknowledgeInputDto("Reviewed and confirmed — see ARH 028 note."));

        ack.IsSuccess.Should().BeTrue();
        ack.Value.Acknowledged.Should().BeTrue();
        ack.Value.AcknowledgementNote.Should().StartWith("Reviewed");
        codes.Should().Contain("CLASSIFICATION.DRIFT_ACKNOWLEDGED");
    }
}
