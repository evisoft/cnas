using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Applications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Applications;

/// <summary>
/// R0322 / TOR UI 014 — tests for
/// <see cref="ApplicationAttachmentService"/>. Covers the attach happy path,
/// conflict on duplicate active link, virus-scan-result flip, and soft-remove.
/// </summary>
public sealed class ApplicationAttachmentServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-app-attach-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(99L);
        c.UserSqid.Returns("SQID-99");
        c.SourceIp.Returns("203.0.113.9");
        c.CorrelationId.Returns("corr-attach");
        return c;
    }

    private static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add(c.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    private static ApplicationAttachmentService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(),
            sqids: NewSqids(),
            caller: NewCaller(),
            audit: audit,
            attachValidator: new ApplicationAttachInputDtoValidator(),
            reasonValidator: new ApplicationAttachmentReasonInputDtoValidator(),
            scanValidator: new ApplicationAttachmentScanResultInputDtoValidator(),
            filterValidator: new ApplicationAttachmentFilterDtoValidator());

    /// <summary>
    /// Seeds an application + a document row and returns their ids and Sqid-equivalents.
    /// </summary>
    /// <param name="db">Shared DbContext.</param>
    /// <returns>Tuple (applicationSqid, documentSqid, applicationId, documentId).</returns>
    private static async Task<(string AppSqid, string DocSqid, long AppId, long DocId)> SeedAsync(CnasDbContext db)
    {
        var application = new ServiceApplication
        {
            SolicitantId = 1,
            ServicePassportId = 1,
            Status = ApplicationStatus.Submitted,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        db.Applications.Add(application);
        var document = new Document
        {
            DossierId = null,
            Kind = DocumentKind.Attachment,
            Title = "ID Card",
            MimeType = "application/pdf",
            SizeBytes = 1024,
            StorageObjectKey = "obj-key-1",
            StorageBucket = "bucket-1",
            ContentSha256Hex = "0".PadRight(64, '0'),
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return ($"SQID-{application.Id}", $"SQID-{document.Id}", application.Id, document.Id);
    }

    [Fact]
    public async Task Attach_HappyPath_PersistsRow_AndAudits()
    {
        using var db = CreateContext();
        var seed = await SeedAsync(db);

        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var result = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Identity",
            IsMandatorySnapshot: true,
            Notes: "Buletin scanat"));

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.ErrorMessage ?? ""));
        var row = db.ApplicationAttachments.Single();
        row.ApplicationId.Should().Be(seed.AppId);
        row.DocumentId.Should().Be(seed.DocId);
        row.Category.Should().Be(ApplicationAttachmentCategory.Identity);
        row.IsMandatorySnapshot.Should().BeTrue();
        row.VirusScanStatus.Should().Be(AttachmentVirusScanStatus.Pending);
        row.RemovedAtUtc.Should().BeNull();
        codes.Should().Contain(IApplicationAttachmentService.AuditAttached);
    }

    [Fact]
    public async Task Attach_DuplicateActiveLink_ReturnsConflict()
    {
        using var db = CreateContext();
        var seed = await SeedAsync(db);

        var audit = NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        // First attach succeeds.
        var first = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Identity",
            IsMandatorySnapshot: false,
            Notes: null));
        first.IsSuccess.Should().BeTrue();

        // Second attach to the same (app, doc) must conflict.
        var second = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Income",
            IsMandatorySnapshot: false,
            Notes: null));
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task RecordVirusScanResult_FlipsPendingToClean()
    {
        using var db = CreateContext();
        var seed = await SeedAsync(db);

        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var attach = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Identity",
            IsMandatorySnapshot: false,
            Notes: null));
        attach.IsSuccess.Should().BeTrue();
        var attachmentSqid = attach.Value!.Id;

        var scan = await svc.RecordVirusScanResultAsync(
            attachmentSqid,
            new ApplicationAttachmentScanResultInputDto(
                Status: "Clean",
                ScannerName: "clamav-0.103",
                Notes: null));

        scan.IsSuccess.Should().BeTrue();
        var row = db.ApplicationAttachments.Single();
        row.VirusScanStatus.Should().Be(AttachmentVirusScanStatus.Clean);
        row.VirusScannedAtUtc.Should().Be(ClockNow);
        row.VirusScannerName.Should().Be("clamav-0.103");
        codes.Should().Contain(IApplicationAttachmentService.AuditVirusScanRecorded);
    }

    [Fact]
    public async Task Remove_SoftRemovesRow()
    {
        using var db = CreateContext();
        var seed = await SeedAsync(db);

        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var attach = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Identity",
            IsMandatorySnapshot: false,
            Notes: null));
        attach.IsSuccess.Should().BeTrue();
        var attachmentSqid = attach.Value!.Id;

        var remove = await svc.RemoveAsync(
            attachmentSqid,
            new ApplicationAttachmentReasonInputDto("Document inlocuit cu o varianta corectata"));

        remove.IsSuccess.Should().BeTrue();
        var row = db.ApplicationAttachments.Single();
        row.RemovedAtUtc.Should().Be(ClockNow);
        row.RemovedByUserId.Should().Be(99L);
        row.RemovalReason.Should().Be("Document inlocuit cu o varianta corectata");
        codes.Should().Contain(IApplicationAttachmentService.AuditRemoved);
    }

    [Fact]
    public async Task List_ExcludesRemovedRowsByDefault()
    {
        using var db = CreateContext();
        var seed = await SeedAsync(db);

        var audit = NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        var attach = await svc.AttachAsync(seed.AppSqid, new ApplicationAttachInputDto(
            DocumentSqid: seed.DocSqid,
            Category: "Identity",
            IsMandatorySnapshot: false,
            Notes: null));
        attach.IsSuccess.Should().BeTrue();

        var remove = await svc.RemoveAsync(
            attach.Value!.Id,
            new ApplicationAttachmentReasonInputDto("Eroare la incarcare"));
        remove.IsSuccess.Should().BeTrue();

        var page = await svc.ListByApplicationAsync(
            seed.AppSqid,
            new ApplicationAttachmentFilterDto(null, null, IncludeRemoved: false, 0, 20));
        page.IsSuccess.Should().BeTrue();
        page.Value!.Items.Should().BeEmpty("removed rows must be hidden when IncludeRemoved is false");

        var pageAll = await svc.ListByApplicationAsync(
            seed.AppSqid,
            new ApplicationAttachmentFilterDto(null, null, IncludeRemoved: true, 0, 20));
        pageAll.Value!.Items.Should().HaveCount(1);
    }
}
