using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Attachments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — service-level tests for
/// <see cref="AttachmentService"/>. Uses EF Core InMemory + NSubstitute + an
/// in-memory <see cref="IBlobStorage"/> stub. Each test exercises one branch
/// of the upload / download / list / archive / delete matrix.
/// </summary>
public sealed class AttachmentServiceTests
{
    /// <summary>Deterministic UTC clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Default caller (uploader) user id.</summary>
    private const long DefaultUploaderId = 4242L;

    /// <summary>Default owner id used in tests.</summary>
    private const long DefaultOwnerId = 999L;

    /// <summary>Sample PDF magic-byte payload.</summary>
    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];

    /// <summary>Sample PNG magic-byte payload.</summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task UploadAsync_HappyPath_PersistsRowAndEmitsSensitiveAudit()
    {
        var harness = Harness.Create();

        var result = await harness.Service.UploadAsync(BuildUpload(PdfBytes));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBeNullOrEmpty();
        result.Value.ContentType.Should().Be("application/pdf");
        result.Value.SizeBytes.Should().Be(PdfBytes.LongLength);
        result.Value.Category.Should().Be(nameof(AttachmentCategory.Income));
        result.Value.SensitivityLabel.Should().Be(nameof(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential));

        var row = await harness.Db.AttachmentRecords.SingleAsync();
        row.OwnerEntityType.Should().Be(AttachmentOwnerTypes.ServiceApplication);
        row.OwnerEntityId.Should().Be(DefaultOwnerId);
        row.UploadedByUserId.Should().Be(DefaultUploaderId);
        row.IsArchived.Should().BeFalse();
        row.IsActive.Should().BeTrue();
        harness.Blobs.Stored.Should().ContainKey(row.StorageKey);

        await harness.Audit.Received().RecordAsync(
            "ATTACHMENT.UPLOADED",
            AuditSeverity.Sensitive,
            Arg.Any<string>(),
            nameof(AttachmentRecord),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_DuplicateContent_ReturnsExistingRow_NoSecondInsert()
    {
        var harness = Harness.Create();

        var first = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        var second = await harness.Service.UploadAsync(BuildUpload(PdfBytes));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Id.Should().Be(first.Value.Id);
        (await harness.Db.AttachmentRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UploadAsync_AnonymousCaller_ReturnsUnauthorized()
    {
        var harness = Harness.Create(callerUserId: null);

        var result = await harness.Service.UploadAsync(BuildUpload(PdfBytes));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task UploadAsync_BadMagicBytes_ReturnsFileTypeMismatch()
    {
        var harness = Harness.Create();

        var result = await harness.Service.UploadAsync(BuildUpload([0xDE, 0xAD, 0xBE, 0xEF]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
        harness.Blobs.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_AsUploader_ReturnsBytesAndEmitsAudit()
    {
        var harness = Harness.Create();
        var uploaded = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        uploaded.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var download = await harness.Service.DownloadAsync(uploaded.Value.Id);

        download.IsSuccess.Should().BeTrue();
        download.Value.Bytes.Should().Equal(PdfBytes);
        download.Value.ContentType.Should().Be("application/pdf");

        await harness.Audit.Received().RecordAsync(
            "ATTACHMENT.DOWNLOADED",
            AuditSeverity.Sensitive,
            Arg.Any<string>(),
            nameof(AttachmentRecord),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadAsync_AsForeignNonStaff_ReturnsForbidden()
    {
        // Uploader uploads first.
        var harness = Harness.Create();
        var uploaded = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        uploaded.IsSuccess.Should().BeTrue();

        // Switch caller to a different user with no staff role.
        harness.Caller.UserId.Returns((long?)9999L);
        harness.Caller.UserSqid.Returns("SQID-9999");
        harness.Caller.Roles.Returns([]);

        var download = await harness.Service.DownloadAsync(uploaded.Value.Id);

        download.IsFailure.Should().BeTrue();
        download.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task ListAsync_ExcludesArchivedAndSoftDeleted()
    {
        var harness = Harness.Create();
        var pdf = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        var png = await harness.Service.UploadAsync(BuildUpload(PngBytes, fileName: "x.png"));
        pdf.IsSuccess.Should().BeTrue();
        png.IsSuccess.Should().BeTrue();
        (await harness.Service.ArchiveAsync(pdf.Value.Id)).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAsync(
            AttachmentOwnerTypes.ServiceApplication, $"SQID-{DefaultOwnerId}");

        list.IsSuccess.Should().BeTrue();
        list.Value.Should().HaveCount(1);
        list.Value[0].Id.Should().Be(png.Value.Id);
    }

    [Fact]
    public async Task ArchiveAsync_SetsIsArchivedAndEmitsNotice()
    {
        var harness = Harness.Create();
        var uploaded = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        uploaded.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var archive = await harness.Service.ArchiveAsync(uploaded.Value.Id);

        archive.IsSuccess.Should().BeTrue();
        var row = await harness.Db.AttachmentRecords.SingleAsync();
        row.IsArchived.Should().BeTrue();
        row.IsActive.Should().BeTrue();

        await harness.Audit.Received().RecordAsync(
            "ATTACHMENT.ARCHIVED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(AttachmentRecord),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesAndEmitsCritical()
    {
        var harness = Harness.Create();
        var uploaded = await harness.Service.UploadAsync(BuildUpload(PdfBytes));
        uploaded.IsSuccess.Should().BeTrue();
        harness.Audit.ClearReceivedCalls();

        var del = await harness.Service.DeleteAsync(uploaded.Value.Id);

        del.IsSuccess.Should().BeTrue();
        var row = await harness.Db.AttachmentRecords.IgnoreQueryFilters().SingleAsync();
        row.IsActive.Should().BeFalse();

        await harness.Audit.Received().RecordAsync(
            "ATTACHMENT.DELETED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(AttachmentRecord),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_UnknownOwnerType_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var dto = BuildUpload(PdfBytes) with { OwnerEntityType = "Lizard" };

        var result = await harness.Service.UploadAsync(dto);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Builds the canonical upload DTO with overrides.</summary>
    /// <param name="bytes">Payload bytes.</param>
    /// <param name="fileName">Optional declared filename.</param>
    /// <returns>The DTO.</returns>
    private static AttachmentUploadDto BuildUpload(byte[] bytes, string fileName = "income-proof.pdf")
        => new(
            OwnerEntityType: AttachmentOwnerTypes.ServiceApplication,
            OwnerSqid: $"SQID-{DefaultOwnerId}",
            ContentBase64: Convert.ToBase64String(bytes),
            DeclaredFileName: fileName,
            Category: nameof(AttachmentCategory.Income),
            SensitivityLabel: nameof(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential),
            Description: null);

    /// <summary>In-memory <see cref="IBlobStorage"/> stub.</summary>
    private sealed class InMemoryBlobs : IBlobStorage
    {
        public Dictionary<string, byte[]> Stored { get; } = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
        {
            Stored[key] = bytes;
            return Task.CompletedTask;
        }

        public Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!Stored.TryGetValue(key, out var bytes))
            {
                throw new FileNotFoundException(key);
            }
            return Task.FromResult(bytes);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Stored.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Stub <see cref="ICnasTimeProvider"/> pinned to <see cref="ClockNow"/>.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Per-test DI harness — owns the InMemory store and stubs.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required AttachmentService Service { get; init; }
        public required InMemoryBlobs Blobs { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }

        public static Harness Create(long? callerUserId = DefaultUploaderId)
        {
            var db = CreateContext();

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var arg = call.Arg<string?>();
                if (!string.IsNullOrEmpty(arg)
                    && arg.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(arg.AsSpan(5), out var n))
                {
                    return Result<long>.Success(n);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(callerUserId);
            caller.UserSqid.Returns(callerUserId is null ? null : $"SQID-{callerUserId}");
            caller.Roles.Returns(callerUserId is null ? [] : ["cnas-user"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var options = Options.Create(new AttachmentOptions());
            var validator = new AttachmentValidator(options);
            var blobs = new InMemoryBlobs();
            var clock = new StubClock();

            var service = new AttachmentService(
                db, blobs, validator, clock, sqids, audit, caller, options);

            return new Harness { Db = db, Service = service, Blobs = blobs, Audit = audit, Caller = caller };
        }
    }

    /// <summary>Creates a fresh EF InMemory context per test.</summary>
    /// <returns>The context.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-attachments-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
