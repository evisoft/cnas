using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.LaborBooklet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0920 / R0921 / TOR BP 2.3 — service-level tests for
/// <see cref="LaborBookletService"/>. Exercises the master-record lifecycle
/// plus the pre-01.01.1999 activity-period add / amend / close paths against
/// an EF Core InMemory store wired with NSubstitute collaborators for audit,
/// caller, clock, Sqid, and attachment service.
/// </summary>
public sealed class LaborBookletServiceTests
{
    /// <summary>Fixed UTC clock used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-labor-booklet-{Guid.NewGuid():N}")
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
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("USR-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-lb");
        caller.Roles.Returns((IReadOnlyCollection<string>)["cnas-user"]);
        return caller;
    }

    /// <summary>Builds an attachment-service mock that succeeds with a canned DTO.</summary>
    private static IAttachmentService NewAttachmentServiceMock(string attachmentSqid = "ATT-1")
    {
        var attachments = Substitute.For<IAttachmentService>();
        var dto = new AttachmentRecordDto(
            Id: attachmentSqid,
            OwnerEntityType: AttachmentOwnerTypes.LaborBooklet,
            OwnerSqid: "SQID-1",
            FileName: "booklet.pdf",
            ContentType: "application/pdf",
            SizeBytes: 4,
            Sha256Hex: "deadbeef",
            Category: nameof(AttachmentCategory.LegalDocument),
            SensitivityLabel: "Confidential",
            Description: null,
            UploadedByUserSqid: "USR-7",
            UploadedUtc: ClockNow,
            IsArchived: false);
        attachments
            .UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AttachmentRecordDto>.Success(dto)));
        return attachments;
    }

    /// <summary>Seeds an active natural-person <see cref="Solicitant"/>.</summary>
    private static async Task<long> SeedSolicitantAsync(CnasDbContext db, string idnp = "2003600012345")
    {
        var hash = IdHashHelper.Hash(idnp);
        var s = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hash,
            DisplayName = "Citizen Test",
            Kind = ApplicantKind.NaturalPerson,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Solicitants.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static LaborBookletService NewService(
        CnasDbContext db,
        IAuditService audit,
        IAttachmentService? attachments = null)
        => new(
            db,
            new StubClock(ClockNow),
            NewSqidMock(),
            NewCaller(),
            audit,
            attachments ?? NewAttachmentServiceMock(),
            new LaborBookletRegisterInputDtoValidator(),
            new LaborBookletVerifyInputDtoValidator(),
            new LaborBookletRejectInputDtoValidator(),
            new ScannedCopyAttachmentInputDtoValidator(),
            new InsuredPersonPre1999PeriodInputDtoValidator());

    // ───────── R0920 — RegisterAsync ─────────

    /// <summary>R0920 — happy path persists Pending booklet + Notice audit.</summary>
    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsPendingAndAuditsNotice()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}",
            CarnetMuncaNumber: "AB-12345",
            IssuedDate: new DateOnly(1990, 1, 1),
            IssuingAuthority: "Cooperativa A"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(LaborBookletStatus.Pending));
        result.Value.CarnetMuncaNumber.Should().Be("AB-12345");

        var persisted = await db.LaborBooklets.SingleAsync();
        persisted.InsuredPersonSolicitantId.Should().Be(sId);
        persisted.Status.Should().Be(LaborBookletStatus.Pending);
        persisted.HasScannedCopy.Should().BeFalse();

        last()!.Value.Code.Should().Be(LaborBookletService.AuditRegistered);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
    }

    /// <summary>R0920 — duplicate (insuredPersonId, CarnetMuncaNumber) returns Conflict.</summary>
    [Fact]
    public async Task RegisterAsync_Duplicate_ReturnsConflictWithStableMessage()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var first = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}",
            CarnetMuncaNumber: "AB-99"));
        first.IsSuccess.Should().BeTrue();

        var dup = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}",
            CarnetMuncaNumber: "AB-99"));

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.Conflict);
        dup.ErrorMessage.Should().Be(LaborBookletService.DuplicateMessage);
    }

    /// <summary>R0920 — registering against an unknown citizen returns NotFound.</summary>
    [Fact]
    public async Task RegisterAsync_UnknownSolicitant_ReturnsNotFound()
    {
        var db = CreateContext();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: "SQID-999999",
            CarnetMuncaNumber: "AB-77"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ───────── R0920 — AttachScannedCopyAsync ─────────

    /// <summary>R0920 — uploads via IAttachmentService + sets HasScannedCopy=true + Notice audit.</summary>
    [Fact]
    public async Task AttachScannedCopyAsync_HappyPath_FlipsFlagAndAudits()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var attachments = NewAttachmentServiceMock();
        var sut = NewService(db, audit, attachments);

        var registered = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-100"));
        registered.IsSuccess.Should().BeTrue();
        var entity = await db.LaborBooklets.SingleAsync();

        var result = await sut.AttachScannedCopyAsync(
            entity.Id,
            new ScannedCopyAttachmentInputDto(
                FileBase64: "VGVzdA==",
                FileName: "booklet.pdf",
                OcrExtractedJson: "{\"name\":\"Ion\"}",
                OcrConfidenceLevel: "High"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasScannedCopy.Should().BeTrue();
        result.Value.OcrConfidenceLevel.Should().Be("High");

        var reloaded = await db.LaborBooklets.SingleAsync(b => b.Id == entity.Id);
        reloaded.HasScannedCopy.Should().BeTrue();
        reloaded.OcrExtractedJson.Should().Be("{\"name\":\"Ion\"}");

        last()!.Value.Code.Should().Be(LaborBookletService.AuditScannedCopyAttached);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
        await attachments.Received(1).UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>());
    }

    // ───────── R0920 — VerifyAsync ─────────

    /// <summary>R0920 — flips Pending -> Verified + Critical audit + counter increments.</summary>
    [Fact]
    public async Task VerifyAsync_HappyPath_TransitionsAndAuditsCritical()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        var registered = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-200"));
        var entity = await db.LaborBooklets.SingleAsync();

        var counterListener = new CounterTap("cnas.labor_booklet.verified");
        var result = await sut.VerifyAsync(entity.Id, "Matched against RSP photo", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(LaborBookletStatus.Verified));

        var reloaded = await db.LaborBooklets.SingleAsync(b => b.Id == entity.Id);
        reloaded.Status.Should().Be(LaborBookletStatus.Verified);
        reloaded.VerifiedAtUtc.Should().Be(ClockNow);
        reloaded.VerifiedByUserId.Should().Be(7L);

        last()!.Value.Code.Should().Be(LaborBookletService.AuditVerified);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
        counterListener.Total.Should().Be(1L);
    }

    /// <summary>R0920 — VerifyAsync against an already-Verified booklet returns Conflict.</summary>
    [Fact]
    public async Task VerifyAsync_AlreadyVerified_ReturnsConflict()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-300"));
        var entity = await db.LaborBooklets.SingleAsync();
        await sut.VerifyAsync(entity.Id, null, CancellationToken.None);

        var second = await sut.VerifyAsync(entity.Id, null, CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>R0920 — flips Pending -> Rejected + Critical audit + RejectionReason persisted.</summary>
    [Fact]
    public async Task RejectAsync_HappyPath_TransitionsAndAuditsCritical()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-400"));
        var entity = await db.LaborBooklets.SingleAsync();

        var result = await sut.RejectAsync(entity.Id, "Illegible scan", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(LaborBookletStatus.Rejected));

        var reloaded = await db.LaborBooklets.SingleAsync(b => b.Id == entity.Id);
        reloaded.Status.Should().Be(LaborBookletStatus.Rejected);
        reloaded.RejectionReason.Should().Be("Illegible scan");
        reloaded.RejectedAtUtc.Should().Be(ClockNow);

        last()!.Value.Code.Should().Be(LaborBookletService.AuditRejected);
        last()!.Value.Severity.Should().Be(AuditSeverity.Critical);
    }

    // ───────── R0921 — AddPeriodAsync ─────────

    /// <summary>R0921 — AddPeriodAsync persists a row tied to the booklet.</summary>
    [Fact]
    public async Task AddPeriodAsync_HappyPath_PersistsRowAndAudits()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-500"));
        var entity = await db.LaborBooklets.SingleAsync();

        var result = await sut.AddPeriodAsync(
            entity.Id,
            new InsuredPersonPre1999PeriodInputDto(
                PeriodStartDate: new DateOnly(1985, 4, 1),
                PeriodEndDate: new DateOnly(1990, 12, 31),
                EmployerName: "Cooperativa de stat",
                Position: "Mecanic",
                DaysWorked: 365),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var period = await db.InsuredPersonPre1999Periods.SingleAsync();
        period.InsuredPersonSolicitantId.Should().Be(sId);
        period.LaborBookletId.Should().Be(entity.Id);
        period.PeriodStartDate.Should().Be(new DateOnly(1985, 4, 1));
        last()!.Value.Code.Should().Be(LaborBookletService.AuditPeriodAdded);
    }

    /// <summary>R0921 — AmendPeriodAsync R0301-supersedes (close prev + insert fresh).</summary>
    [Fact]
    public async Task AmendPeriodAsync_HappyPath_SupersedesViaValidFromTo()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-600"));
        var booklet = await db.LaborBooklets.SingleAsync();
        await sut.AddPeriodAsync(booklet.Id, new InsuredPersonPre1999PeriodInputDto(
            PeriodStartDate: new DateOnly(1985, 1, 1),
            PeriodEndDate: new DateOnly(1989, 12, 31),
            EmployerName: "Old name"), CancellationToken.None);
        var previous = await db.InsuredPersonPre1999Periods.SingleAsync();

        var result = await sut.AmendPeriodAsync(
            previous.Id,
            new InsuredPersonPre1999PeriodInputDto(
                PeriodStartDate: new DateOnly(1985, 1, 1),
                PeriodEndDate: new DateOnly(1989, 12, 31),
                EmployerName: "Corrected name",
                ChangeReason: "Typo on employer name"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var all = await db.InsuredPersonPre1999Periods.OrderBy(p => p.Id).ToListAsync();
        all.Should().HaveCount(2);
        all[0].ValidToUtc.Should().Be(ClockNow); // previous row was closed
        all[0].EmployerName.Should().Be("Old name");
        all[1].ValidToUtc.Should().BeNull();      // fresh row is open
        all[1].EmployerName.Should().Be("Corrected name");
    }

    /// <summary>R0921 — ClosePeriodAsync sets ValidToUtc + Notice audit.</summary>
    [Fact]
    public async Task ClosePeriodAsync_HappyPath_SetsValidToAndAudits()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-700"));
        var booklet = await db.LaborBooklets.SingleAsync();
        await sut.AddPeriodAsync(booklet.Id, new InsuredPersonPre1999PeriodInputDto(
            PeriodStartDate: new DateOnly(1985, 1, 1),
            PeriodEndDate: new DateOnly(1989, 12, 31)), CancellationToken.None);
        var period = await db.InsuredPersonPre1999Periods.SingleAsync();

        var result = await sut.ClosePeriodAsync(period.Id, "Operator close — incorrect entry", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await db.InsuredPersonPre1999Periods.SingleAsync(p => p.Id == period.Id);
        reloaded.ValidToUtc.Should().Be(ClockNow);
        last()!.Value.Code.Should().Be(LaborBookletService.AuditPeriodClosed);
        last()!.Value.Severity.Should().Be(AuditSeverity.Notice);
    }

    // ───────── Validator boundary tests ─────────

    /// <summary>R0921 — period end date past 1998-12-31 is refused at the validator boundary.</summary>
    [Fact]
    public async Task AddPeriodAsync_PeriodEndPast1998_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-800"));
        var booklet = await db.LaborBooklets.SingleAsync();

        var result = await sut.AddPeriodAsync(
            booklet.Id,
            new InsuredPersonPre1999PeriodInputDto(
                PeriodStartDate: new DateOnly(1998, 1, 1),
                PeriodEndDate: new DateOnly(1999, 1, 1)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>R0920 — lower-case CarnetMuncaNumber violates the alphabet pattern.</summary>
    [Fact]
    public async Task RegisterAsync_LowercaseCarnetMunca_ReturnsValidationFailed()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        var result = await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}",
            CarnetMuncaNumber: "ab-100"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ───────── Listing ─────────

    /// <summary>R0921 — ListPeriodsForInsuredPersonAsync returns rows in ascending PeriodStartDate.</summary>
    [Fact]
    public async Task ListPeriodsForInsuredPersonAsync_ReturnsAscendingByStartDate()
    {
        var db = CreateContext();
        var sId = await SeedSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, audit);

        await sut.RegisterAsync(new LaborBookletRegisterInputDto(
            InsuredPersonSqid: $"SQID-{sId}", CarnetMuncaNumber: "AB-900"));
        var booklet = await db.LaborBooklets.SingleAsync();

        await sut.AddPeriodAsync(booklet.Id, new InsuredPersonPre1999PeriodInputDto(
            PeriodStartDate: new DateOnly(1992, 6, 1),
            PeriodEndDate: new DateOnly(1995, 5, 31)), CancellationToken.None);
        await sut.AddPeriodAsync(booklet.Id, new InsuredPersonPre1999PeriodInputDto(
            PeriodStartDate: new DateOnly(1985, 1, 1),
            PeriodEndDate: new DateOnly(1989, 12, 31)), CancellationToken.None);
        await sut.AddPeriodAsync(booklet.Id, new InsuredPersonPre1999PeriodInputDto(
            PeriodStartDate: new DateOnly(1990, 1, 1),
            PeriodEndDate: new DateOnly(1992, 5, 31)), CancellationToken.None);

        var list = await sut.ListPeriodsForInsuredPersonAsync(sId, CancellationToken.None);

        list.Should().HaveCount(3);
        list[0].PeriodStartDate.Should().Be(new DateOnly(1985, 1, 1));
        list[1].PeriodStartDate.Should().Be(new DateOnly(1990, 1, 1));
        list[2].PeriodStartDate.Should().Be(new DateOnly(1992, 6, 1));
    }

    // ───────── Counter tap helper ─────────

    /// <summary>
    /// Tiny <see cref="MeterListener"/> that totals every measurement emitted
    /// by a single named counter for the duration of the test. Disposed
    /// implicitly when the test ends.
    /// </summary>
    private sealed class CounterTap
    {
        private long _total;

        /// <summary>Total of all measurements observed since construction.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <summary>Starts listening for measurements on the named counter.</summary>
        /// <param name="counterName">Fully qualified counter name (e.g. <c>cnas.labor_booklet.verified</c>).</param>
        public CounterTap(string counterName)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName && instrument.Name == counterName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
                Interlocked.Add(ref _total, measurement));
            listener.Start();
        }
    }
}
