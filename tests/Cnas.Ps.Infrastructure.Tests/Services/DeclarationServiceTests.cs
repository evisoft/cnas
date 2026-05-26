using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services.Declarations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0810 / R0811 / R0812 — service-level tests for <see cref="DeclarationService"/>.
/// Exercises the three registration paths plus the adjust / cancel lifecycle
/// against an EF Core InMemory store wired with NSubstitute collaborators for
/// audit, caller, clock, and Sqid.
/// </summary>
public sealed class DeclarationServiceTests
{
    /// <summary>Fixed UTC clock used by every test (2026-05-22 12:00 UTC).</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Canonical first-of-month anchor (April 2026 — distinct from the clock month).</summary>
    private static readonly DateOnly ReportingMonth = new(2026, 4, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-declarations-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Sqid mock that round-trips between "SQID-{id}" strings and bigint ids.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity, string? Details, long? TargetId)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity, string? Details, long? TargetId)? slot = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                slot = (
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<string>(5),
                    call.ArgAt<long?>(4));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Authenticated-caller helper.</summary>
    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-decl");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Seeds an active <see cref="Contributor"/> and returns its bigint id.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = "1003600012346",
            IdnoHash = IdHashHelper.Hash("1003600012346"),
            Denumire = "SRL Test",
            CreatedAtUtc = ClockNow.AddDays(-10),
            RegisteredAtUtc = ClockNow.AddDays(-10),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static DeclarationService NewService(
        CnasDbContext db,
        IAuditService audit,
        Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService? periods = null,
        IAttachmentService? attachments = null)
        => new(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            periods ?? NewOpenPeriods(),
            new DeclarationFromSfsInputDtoValidator(),
            new DeclarationAtCnasInputDtoValidator(),
            new DeclarationFromOtherDocumentInputDtoValidator(),
            new DeclarationAdjustInputDtoValidator(),
            new DeclarationCancelInputDtoValidator(),
            new ScannedDeclarationAttachmentInputDtoValidator(),
            new DeclarationsSearchInputValidator(),
            attachments ?? Substitute.For<IAttachmentService>(),
            new QbeToLinqConverter(new QbeRegistrySchemaProvider()),
            new QueryBudgetService(
                new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance),
                NullLogger<QueryBudgetService>.Instance));

    /// <summary>Stub <see cref="Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService"/> that treats every month as open.</summary>
    private static Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService NewOpenPeriods()
    {
        var periods = Substitute.For<Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService>();
        periods.IsMonthClosedAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        return periods;
    }

    // ───────── R0810 / BP 1.2-A — RegisterFromSfsAsync ─────────

    /// <summary>R0810 — happy path persists the row and writes an Information audit prefix.</summary>
    [Fact]
    public async Task RegisterFromSfsAsync_HappyPath_PersistsAndAudits()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: 5000m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(nameof(DeclarationKind.Sfs));
        result.Value.DeclaredContributionAmount.Should().Be(5000m);
        var persisted = await db.Declarations.SingleAsync();
        persisted.ContributorId.Should().Be(contributorId);
        persisted.Status.Should().Be(DeclarationStatus.Received);
        last()!.Value.Code.Should().Be("DECLARATION.REGISTERED.Sfs");
    }

    /// <summary>R0810 — duplicate (ContributorId, Kind=Sfs, Month, Ref) returns Conflict.</summary>
    [Fact]
    public async Task RegisterFromSfsAsync_Duplicate_ReturnsConflict()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: 100m));
        first.IsSuccess.Should().BeTrue();

        var dup = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: 200m));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
        dup.ErrorMessage.Should().Be(DeclarationService.DuplicateMessage);
    }

    // ───────── R0811 / BP 1.2-B — RegisterAtCnasAsync ─────────

    /// <summary>R0811 — rejects Kind=Sfs at the validator boundary.</summary>
    [Fact]
    public async Task RegisterAtCnasAsync_SfsKind_Fails()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAtCnasAsync(new DeclarationAtCnasInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            Kind: "Sfs",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "FORM-1",
            DeclaredContributionAmount: 100m));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ───────── R0812 / BP 1.2-C — RegisterFromOtherDocumentAsync ─────────

    /// <summary>R0812 — accepts Kind=Control.</summary>
    [Fact]
    public async Task RegisterFromOtherDocumentAsync_Control_Persists()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterFromOtherDocumentAsync(new DeclarationFromOtherDocumentInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            Kind: "Control",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "CTRL-2026-001",
            DeclaredContributionAmount: 200m));

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(nameof(DeclarationKind.Control));
        last()!.Value.Code.Should().Be("DECLARATION.REGISTERED.Control");
    }

    // ───────── Adjust / Cancel ─────────

    /// <summary>AdjustAsync sets AdjustedContributionAmount + status=Adjusted; Notice audit.</summary>
    [Fact]
    public async Task AdjustAsync_HappyPath_SetsAdjustedAndAudits()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-X",
            DeclaredContributionAmount: 500m));
        registered.IsSuccess.Should().BeTrue();
        var entity = await db.Declarations.SingleAsync();

        var result = await sut.AdjustAsync(entity.Id, 450m, "Operator correction", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AdjustedContributionAmount.Should().Be(450m);
        result.Value.Status.Should().Be(nameof(DeclarationStatus.Adjusted));
        last()!.Value.Code.Should().Be(DeclarationService.AuditAdjusted);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
    }

    /// <summary>AdjustAsync against an already-cancelled row returns Conflict.</summary>
    [Fact]
    public async Task AdjustAsync_AlreadyCancelled_ReturnsConflict()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-Y",
            DeclaredContributionAmount: 500m));
        registered.IsSuccess.Should().BeTrue();
        var entity = await db.Declarations.SingleAsync();
        await sut.CancelAsync(entity.Id, "Operator cancel", CancellationToken.None);

        var result = await sut.AdjustAsync(entity.Id, 100m, "Late adjust attempt", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>CancelAsync transitions to Cancelled status.</summary>
    [Fact]
    public async Task CancelAsync_HappyPath_SetsCancelled()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-Z",
            DeclaredContributionAmount: 500m));
        var entity = await db.Declarations.SingleAsync();

        var result = await sut.CancelAsync(entity.Id, "Operator cancel reason", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.Declarations.SingleAsync(d => d.Id == entity.Id);
        reloaded.Status.Should().Be(DeclarationStatus.Cancelled);
    }

    // ───────── R0820 / BP 1.2-K — closed-month guard ─────────

    /// <summary>R0820 — RegisterFromSfsAsync refuses with MONTH_CLOSED when the month is closed.</summary>
    [Fact]
    public async Task RegisterFromSfsAsync_ClosedMonth_RefusesWithMonthClosed()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var closedPeriods = Substitute.For<Cnas.Ps.Application.ManagementPeriods.IManagementPeriodService>();
        closedPeriods
            .IsMonthClosedAsync(ReportingMonth, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var sut = NewService(db, audit, closedPeriods);

        var result = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-CLOSED",
            DeclaredContributionAmount: 100m));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be(DeclarationService.MonthClosedMessage);
        (await db.Declarations.CountAsync()).Should().Be(0);
    }

    // ───────── R0821 — AttachScannedCopyAsync ─────────

    /// <summary>Builds an attachment-service mock that succeeds with a canned DTO.</summary>
    private static IAttachmentService NewAttachmentServiceMock(string attachmentSqid = "ATT-1")
    {
        var attachments = Substitute.For<IAttachmentService>();
        var dto = new AttachmentRecordDto(
            Id: attachmentSqid,
            OwnerEntityType: "Declaration",
            OwnerSqid: "SQID-1",
            FileName: "form.pdf",
            ContentType: "application/pdf",
            SizeBytes: 4,
            Sha256Hex: "deadbeef",
            Category: nameof(AttachmentCategory.LegalDocument),
            SensitivityLabel: "Confidential",
            Description: null,
            UploadedByUserSqid: "USR-1",
            UploadedUtc: ClockNow,
            IsArchived: false);
        attachments
            .UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AttachmentRecordDto>.Success(dto)));
        return attachments;
    }

    /// <summary>R0821 — happy path: blob uploaded, HasScannedCopy=true, OCR persisted, audit emitted.</summary>
    [Fact]
    public async Task AttachScannedCopyAsync_HappyPath_PersistsAndAudits()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, last) = NewAuditCapture();
        var attachments = NewAttachmentServiceMock();
        var sut = NewService(db, audit, attachments: attachments);

        var registered = await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-SCAN",
            DeclaredContributionAmount: 100m));
        registered.IsSuccess.Should().BeTrue();
        var entity = await db.Declarations.SingleAsync();

        var result = await sut.AttachScannedCopyAsync(
            entity.Id,
            new ScannedDeclarationAttachmentInputDto(
                FileBase64: "VGVzdA==",
                FileName: "form.pdf",
                OcrExtractedJson: "{\"k\":\"v\"}",
                OcrConfidenceLevel: "High"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasScannedCopy.Should().BeTrue();
        result.Value.OcrConfidenceLevel.Should().Be("High");
        var reloaded = await db.Declarations.SingleAsync(d => d.Id == entity.Id);
        reloaded.HasScannedCopy.Should().BeTrue();
        reloaded.OcrExtractedJson.Should().Be("{\"k\":\"v\"}");
        last()!.Value.Code.Should().Be(DeclarationService.AuditScannedCopyAttached);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
        await attachments.Received(1).UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R0821 — attaching to a cancelled declaration is refused with Conflict.</summary>
    [Fact]
    public async Task AttachScannedCopyAsync_CancelledDeclaration_ReturnsConflict()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-SCAN-CXL",
            DeclaredContributionAmount: 50m));
        var entity = await db.Declarations.SingleAsync();
        await sut.CancelAsync(entity.Id, "Operator cancel reason", CancellationToken.None);

        var result = await sut.AttachScannedCopyAsync(
            entity.Id,
            new ScannedDeclarationAttachmentInputDto(FileBase64: "VGVzdA==", FileName: "f.pdf"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>R0821 — malformed OcrExtractedJson (too long) is refused by the validator with ValidationFailed.</summary>
    [Fact]
    public async Task AttachScannedCopyAsync_OversizedOcr_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-SCAN-OCR",
            DeclaredContributionAmount: 50m));
        var entity = await db.Declarations.SingleAsync();

        var oversized = new string('x', 100_001);
        var result = await sut.AttachScannedCopyAsync(
            entity.Id,
            new ScannedDeclarationAttachmentInputDto(
                FileBase64: "VGVzdA==",
                FileName: "f.pdf",
                OcrExtractedJson: oversized),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>R0821 — bad OcrConfidenceLevel is refused by the validator with ValidationFailed.</summary>
    [Fact]
    public async Task AttachScannedCopyAsync_BadConfidence_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: ReportingMonth,
            ReferenceNumber: "SFS-SCAN-CONF",
            DeclaredContributionAmount: 50m));
        var entity = await db.Declarations.SingleAsync();

        var result = await sut.AttachScannedCopyAsync(
            entity.Id,
            new ScannedDeclarationAttachmentInputDto(
                FileBase64: "VGVzdA==",
                FileName: "f.pdf",
                OcrConfidenceLevel: "Excellent"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ───────── R0822 — SearchAsync ─────────

    /// <summary>R0822 — happy path returns a paged list scoped to the QBE kind filter.</summary>
    [Fact]
    public async Task SearchAsync_HappyPath_ReturnsPagedList()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        // Seed 3 declarations spread across distinct months.
        for (var i = 0; i < 3; i++)
        {
            await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
                ContributorSqid: $"SQID-{contributorId}",
                ReportingMonth: new DateOnly(2026, i + 1, 1),
                ReferenceNumber: $"SFS-{i}",
                DeclaredContributionAmount: 10m * (i + 1)));
        }

        var filter = new QbeFilterDto(
            Combinator: "AND",
            Conditions: new[]
            {
                new QbeConditionDto("Kind", "Equals", nameof(DeclarationKind.Sfs)),
            });

        var result = await sut.SearchAsync(new DeclarationsSearchInput(Filter: filter), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(3);
        result.Value.TotalCount.Should().Be(3);
    }

    /// <summary>R0822 — QBE filter on Kind narrows the result set.</summary>
    [Fact]
    public async Task SearchAsync_QbeFilter_NarrowsResults()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: new DateOnly(2026, 1, 1),
            ReferenceNumber: "SFS-A",
            DeclaredContributionAmount: 100m));
        await sut.RegisterAtCnasAsync(new DeclarationAtCnasInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            Kind: "Bass",
            ReportingMonth: new DateOnly(2026, 2, 1),
            ReferenceNumber: "BASS-A",
            DeclaredContributionAmount: 200m));

        var filter = new QbeFilterDto(
            Combinator: "AND",
            Conditions: new[]
            {
                new QbeConditionDto("Kind", "Equals", nameof(DeclarationKind.Bass)),
            });

        var result = await sut.SearchAsync(new DeclarationsSearchInput(Filter: filter), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Kind.Should().Be("Bass");
    }

    /// <summary>R0822 — FromUtc/ToUtc filter narrows by FiledAtUtc window.</summary>
    [Fact]
    public async Task SearchAsync_DateFilter_NarrowsResults()
    {
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: new DateOnly(2026, 1, 1),
            ReferenceNumber: "SFS-1",
            DeclaredContributionAmount: 1m,
            FiledAtUtc: new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
            ContributorSqid: $"SQID-{contributorId}",
            ReportingMonth: new DateOnly(2026, 2, 1),
            ReferenceNumber: "SFS-2",
            DeclaredContributionAmount: 1m,
            FiledAtUtc: new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc)));

        var filter = new QbeFilterDto(
            Combinator: "AND",
            Conditions: new[]
            {
                new QbeConditionDto("Kind", "Equals", nameof(DeclarationKind.Sfs)),
            });

        var result = await sut.SearchAsync(
            new DeclarationsSearchInput(
                Filter: filter,
                FromUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ToUtc: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].ReferenceNumber.Should().Be("SFS-2");
    }

    /// <summary>R0822 — Take above 200 is rejected by the input validator.</summary>
    [Fact]
    public async Task SearchAsync_TakeAboveCap_ReturnsValidationFailed()
    {
        var db = CreateContext();
        await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit, attachments: NewAttachmentServiceMock());

        var result = await sut.SearchAsync(new DeclarationsSearchInput(Take: 500), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>R0822 — wide-open call (no Kind, no payer, no QBE) is allowed only when row count fits in budget.</summary>
    [Fact]
    public async Task SearchAsync_TooBroadCall_ReturnsQueryTooBroad()
    {
        // Custom infra to inject a tight budget (3) and seed > budget rows
        // without a filter. Mirrors the SolicitantServiceQueryBudgetTests pattern.
        var db = CreateContext();
        var contributorId = await SeedContributorAsync(db);
        var (audit, _) = NewAuditCapture();
        var attachments = NewAttachmentServiceMock();
        var tightBudget = new QueryBudgetService(
            new SingleBudgetForDeclarationPolicy(budget: 3),
            NullLogger<QueryBudgetService>.Instance);
        var sut = new DeclarationService(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            NewOpenPeriods(),
            new DeclarationFromSfsInputDtoValidator(),
            new DeclarationAtCnasInputDtoValidator(),
            new DeclarationFromOtherDocumentInputDtoValidator(),
            new DeclarationAdjustInputDtoValidator(),
            new DeclarationCancelInputDtoValidator(),
            new ScannedDeclarationAttachmentInputDtoValidator(),
            new DeclarationsSearchInputValidator(),
            attachments,
            new QbeToLinqConverter(new QbeRegistrySchemaProvider()),
            tightBudget);

        for (var i = 0; i < 5; i++)
        {
            await sut.RegisterFromSfsAsync(new DeclarationFromSfsInputDto(
                ContributorSqid: $"SQID-{contributorId}",
                ReportingMonth: new DateOnly(2026, (i % 12) + 1, 1),
                ReferenceNumber: $"SFS-T-{i}",
                DeclaredContributionAmount: 1m));
        }

        var result = await sut.SearchAsync(new DeclarationsSearchInput(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
    }

    /// <summary>
    /// Single-registry budget policy that returns a uniform <paramref name="budget"/>
    /// for the Declaration registry — used by <see cref="SearchAsync_TooBroadCall_ReturnsQueryTooBroad"/>
    /// to inject a tight budget without touching the static policy.
    /// </summary>
    /// <param name="budget">Row budget to apply.</param>
    private sealed class SingleBudgetForDeclarationPolicy(int budget) : IQueryBudgetPolicy
    {
        /// <inheritdoc />
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
