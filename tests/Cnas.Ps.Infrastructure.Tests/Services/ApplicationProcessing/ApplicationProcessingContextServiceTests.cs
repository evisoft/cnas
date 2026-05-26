using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ApplicationProcessing;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ApplicationProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.ApplicationProcessing;

/// <summary>
/// R0701 / TOR CF 21.01-02 — service-level tests for
/// <see cref="ApplicationProcessingContextService"/>. Pins the aggregation
/// contract (applicant profile + open tasks + decision drafts + attachments +
/// audit timeline + suggested next actions + pre-fill hint), the permission
/// gate (process permission OR assigned examiner OR admin), the audit row,
/// and the per-call counter increment.
/// </summary>
public sealed class ApplicationProcessingContextServiceTests
{
    /// <summary>Fixed UTC clock instant used across every test (2026-05-22 12:00 UTC).</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stable IDNP / IDNP-hash pair used to link Solicitant + InsuredPerson.</summary>
    private const string TestIdnp = "2000123456789";

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-r0701-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Fixed-instant clock substitute.</summary>
    private static ICnasTimeProvider NewClockMock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    /// <summary>Authenticated-caller helper.</summary>
    private static ICallerContext NewCaller(long userId, params string[] roles)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(userId);
        caller.UserSqid.Returns($"USR-{userId}");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-r0701");
        caller.Roles.Returns(roles);
        caller.AccessScope.Returns(Cnas.Ps.Infrastructure.AccessScope.RolesBasedAccessScope.Unscoped);
        return caller;
    }

    /// <summary>Audit capture — exposes the most-recent invocation.</summary>
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

    /// <summary>Pre-fill stub — defaults to returning an empty payload (no candidates).</summary>
    private static IPrefillService NewPrefillStub(int fieldCount = 0)
    {
        var prefill = Substitute.For<IPrefillService>();
        var fields = new Dictionary<string, PrefillFieldDto>(StringComparer.Ordinal);
        for (var i = 0; i < fieldCount; i++)
        {
            fields[$"field{i}"] = new PrefillFieldDto($"v{i}", "RSP", ClockNow);
        }
        var payload = new PrefillPayloadDto(
            SolicitantSqid: "SQID-100",
            Fields: fields,
            Warnings: Array.Empty<string>(),
            GeneratedAtUtc: ClockNow,
            SourceUsedPerField: new Dictionary<string, string>(StringComparer.Ordinal));
        prefill.PrefillForSolicitantAsync(
                Arg.Any<long>(),
                Arg.Any<PrefillRequestDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<PrefillPayloadDto>.Success(payload)));
        return prefill;
    }

    /// <summary>Seeds a Solicitant, InsuredPerson, and current address/contact/civil-status rows.</summary>
    private static async Task<(long SolicitantId, long InsuredPersonId)> SeedApplicantAsync(CnasDbContext db)
    {
        var hash = IdHashHelper.Hash(TestIdnp);
        var solicitant = new Solicitant
        {
            NationalId = TestIdnp,
            NationalIdHash = hash,
            DisplayName = "Maria Ionescu",
            Kind = ApplicantKind.NaturalPerson,
            Email = "maria@example.test",
            PhoneE164 = "+37369123456",
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);

        var ip = new InsuredPerson
        {
            Idnp = TestIdnp,
            IdnpHash = hash,
            FirstName = "Maria",
            LastName = "Ionescu",
            BirthDate = new DateOnly(1985, 6, 15),
            RegisteredAtUtc = ClockNow.AddYears(-10),
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.InsuredPersons.Add(ip);
        await db.SaveChangesAsync();

        db.ContributorAddresses.Add(new ContributorAddress
        {
            ContributorId = ip.Id,
            Street = "Str. Pacii 12",
            City = "Chisinau",
            Region = "Chisinau",
            PostalCode = "MD2001",
            Country = "MD",
            ValidFromUtc = ClockNow.AddDays(-30),
            ValidToUtc = null,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        });
        db.ContributorContacts.Add(new ContributorContact
        {
            ContributorId = ip.Id,
            PhoneE164 = "+37369123456",
            Email = "maria@example.test",
            ValidFromUtc = ClockNow.AddDays(-30),
            ValidToUtc = null,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        });
        db.ContributorCivilStatuses.Add(new ContributorCivilStatus
        {
            ContributorId = ip.Id,
            Status = CivilStatusType.Married,
            EffectiveDate = new DateOnly(2010, 4, 1),
            ValidFromUtc = ClockNow.AddDays(-30),
            ValidToUtc = null,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        });
        db.ContributorActivityPeriods.Add(new ContributorActivityPeriod
        {
            ContributorId = ip.Id,
            EmployerCode = "EMP-001",
            Position = "Tester",
            MonthlySalary = 7500m,
            ValidFromUtc = ClockNow.AddDays(-100),
            ValidToUtc = null,
            CreatedAtUtc = ClockNow.AddDays(-100),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        return (solicitant.Id, ip.Id);
    }

    /// <summary>Seeds a ServicePassport + ServiceApplication + Dossier triple.</summary>
    private static async Task<(long AppId, long DossierId)> SeedApplicationAsync(
        CnasDbContext db,
        long solicitantId,
        ApplicationStatus status,
        long? assignedExaminerId = null)
    {
        var passport = new ServicePassport
        {
            CreatedAtUtc = ClockNow.AddDays(-60),
            Code = $"SP-{Guid.NewGuid():N}"[..16],
            NameRo = "Test passport",
            DescriptionRo = "Test",
            FormSchemaJson = "{}",
            WorkflowCode = "WF-TEST",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow.AddDays(-5),
            SolicitantId = solicitantId,
            ServicePassportId = passport.Id,
            Status = status,
            FormPayloadJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-5),
            IsActive = true,
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();

        var dossier = new Dossier
        {
            CreatedAtUtc = ClockNow.AddDays(-5),
            ApplicationId = app.Id,
            DossierNumber = $"D-{app.Id}",
            AssignedExaminerId = assignedExaminerId,
            IsActive = true,
        };
        db.Dossiers.Add(dossier);
        await db.SaveChangesAsync();

        return (app.Id, dossier.Id);
    }

    /// <summary>Seeds one workflow task on a dossier.</summary>
    private static async Task<long> SeedTaskAsync(
        CnasDbContext db,
        long dossierId,
        WorkflowTaskStatus status,
        string title = "Examinare cerere")
    {
        var task = new WorkflowTask
        {
            CreatedAtUtc = ClockNow.AddDays(-2),
            DossierId = dossierId,
            Title = title,
            Status = status,
            GroupCode = "cnas-examiner",
            IsActive = true,
        };
        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    /// <summary>Seeds one decision Document linked via dossier.</summary>
    private static async Task<long> SeedDecisionAsync(
        CnasDbContext db,
        long dossierId,
        bool isSigned = false)
    {
        var doc = new Document
        {
            CreatedAtUtc = ClockNow.AddDays(-1),
            DossierId = dossierId,
            Kind = DocumentKind.Decision,
            Title = "Decizia draft",
            MimeType = "application/pdf",
            SizeBytes = 1024,
            StorageObjectKey = $"docs/decision-{Guid.NewGuid():N}",
            StorageBucket = "cnas-test",
            ContentSha256Hex = new string('a', 64),
            IsSigned = isSigned,
            IsActive = true,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    /// <summary>Seeds one attachment owned by the application.</summary>
    private static async Task<long> SeedAttachmentAsync(
        CnasDbContext db,
        long applicationId,
        string fileName,
        DateTime uploadedUtc,
        int sizeBytes = 512)
    {
        var att = new AttachmentRecord
        {
            OwnerEntityType = "ServiceApplication",
            OwnerEntityId = applicationId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = sizeBytes,
            StorageKey = $"att/{Guid.NewGuid():N}",
            Sha256Hex = new string('b', 64),
            Category = AttachmentCategory.Other,
            SensitivityLevel = 2,
            UploadedByUserId = 1L,
            UploadedUtc = uploadedUtc,
            CreatedAtUtc = uploadedUtc,
            IsActive = true,
        };
        db.AttachmentRecords.Add(att);
        await db.SaveChangesAsync();
        return att.Id;
    }

    /// <summary>Seeds one audit-log row scoped to the supplied application id.</summary>
    private static AuditLog BuildAuditRow(
        long applicationId,
        string eventCode,
        AuditSeverity severity,
        DateTime when,
        string? details = null,
        string? actor = null)
        => new()
        {
            EventCode = eventCode,
            ActorId = actor ?? "system",
            EventAtUtc = when,
            CreatedAtUtc = when,
            Severity = severity,
            TargetEntity = nameof(ServiceApplication),
            TargetEntityId = applicationId,
            DetailsJson = details ?? "{}",
            PrevHash = "GENESIS",
            RowHash = new string('0', 64),
            IsActive = true,
        };

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static ApplicationProcessingContextService NewService(
        CnasDbContext db,
        ICallerContext caller,
        IAuditService audit,
        IPrefillService? prefill = null)
        => new(
            db,
            NewClockMock(),
            NewSqidMock(),
            caller,
            audit,
            prefill ?? NewPrefillStub(),
            NullLogger<ApplicationProcessingContextService>.Instance);

    // ───────────────────────── Tests ─────────────────────────

    /// <summary>R0701 / Test 1 — Returns the application + applicant profile.</summary>
    [Fact]
    public async Task R0701_ReturnsApplicationWithApplicantProfile()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.ApplicationSqid.Should().Be($"SQID-{appId}");
        result.Value.Status.Should().Be("Submitted");
        result.Value.Applicant.SolicitantSqid.Should().Be($"SQID-{solId}");
        result.Value.Applicant.DisplayName.Should().Be("Maria Ionescu");
        result.Value.Applicant.Email.Should().Be("maria@example.test");
        result.Value.Applicant.PhoneE164.Should().Be("+37369123456");
        result.Value.Applicant.CurrentAddress.Should().NotBeNull();
        result.Value.Applicant.CurrentAddress!.City.Should().Be("Chisinau");
        result.Value.Applicant.RecentActivityPeriods.Should().HaveCount(1);
        result.Value.GeneratedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>R0701 / Test 2 — Open tasks filter excludes Completed/Cancelled.</summary>
    [Fact]
    public async Task R0701_OpenTasks_ExcludesTerminalStatuses()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, dossierId) = await SeedApplicationAsync(db, solId, ApplicationStatus.UnderExamination);
        await SeedTaskAsync(db, dossierId, WorkflowTaskStatus.Pending, "Open-Pending");
        await SeedTaskAsync(db, dossierId, WorkflowTaskStatus.InProgress, "Open-InProgress");
        await SeedTaskAsync(db, dossierId, WorkflowTaskStatus.Overdue, "Open-Overdue");
        await SeedTaskAsync(db, dossierId, WorkflowTaskStatus.Completed, "Done");
        await SeedTaskAsync(db, dossierId, WorkflowTaskStatus.Cancelled, "Cancelled");
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.OpenTasks.Should().HaveCount(3);
        result.Value.OpenTasks.Select(t => t.Title)
            .Should().BeEquivalentTo(ExpectedOpenTaskTitles);
    }

    /// <summary>Static expected-title set — extracted to satisfy CA1861.</summary>
    private static readonly string[] ExpectedOpenTaskTitles =
        ["Open-Pending", "Open-InProgress", "Open-Overdue"];

    /// <summary>R0701 / Test 3 — Decision drafts exclude finalised (signed) decisions.</summary>
    [Fact]
    public async Task R0701_DecisionDrafts_ExcludeSignedDocuments()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, dossierId) = await SeedApplicationAsync(db, solId, ApplicationStatus.UnderExamination);
        var draftId = await SeedDecisionAsync(db, dossierId, isSigned: false);
        await SeedDecisionAsync(db, dossierId, isSigned: true);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.DecisionDrafts.Should().HaveCount(1);
        result.Value.DecisionDrafts[0].DecisionSqid.Should().Be($"SQID-{draftId}");
        result.Value.DecisionDrafts[0].Status.Should().Be("Draft");
    }

    /// <summary>R0701 / Test 4 — Attachments capped at 20, newest first.</summary>
    [Fact]
    public async Task R0701_Attachments_Top20_NewestFirst()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        // Seed 25 attachments at staggered timestamps.
        for (var i = 0; i < 25; i++)
        {
            await SeedAttachmentAsync(db, appId, $"file-{i}.pdf", ClockNow.AddHours(-i));
        }
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Attachments.Should().HaveCount(20, "the service caps the attachment list at 20.");
        // Newest first: the most recent UploadedUtc is i=0 (ClockNow - 0h).
        result.Value.Attachments[0].FileName.Should().Be("file-0.pdf");
        result.Value.Attachments[19].FileName.Should().Be("file-19.pdf");
    }

    /// <summary>R0701 / Test 5 — Audit timeline last 50 rows for the application.</summary>
    [Fact]
    public async Task R0701_AuditTimeline_Last50RowsForApplication()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        // Seed 60 audit rows scoped to this application, plus 5 rows for a different app
        // to confirm the scope filter.
        for (var i = 0; i < 60; i++)
        {
            db.AuditLogs.Add(BuildAuditRow(appId, "APPLICATION.PROBE", AuditSeverity.Information,
                ClockNow.AddMinutes(-i)));
        }
        for (var i = 0; i < 5; i++)
        {
            db.AuditLogs.Add(BuildAuditRow(applicationId: appId + 999, "APPLICATION.OTHER",
                AuditSeverity.Information, ClockNow.AddMinutes(-i)));
        }
        await db.SaveChangesAsync();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AuditTimeline.Should().HaveCount(50);
        result.Value.AuditTimeline.Should().OnlyContain(e => e.EventCode == "APPLICATION.PROBE");
    }

    /// <summary>R0701 / Test 6 — Audit timeline detail strings are PII-redacted.</summary>
    [Fact]
    public async Task R0701_AuditTimeline_DetailIsPiiRedacted()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        db.AuditLogs.Add(BuildAuditRow(appId, "PROFILE.UPDATED", AuditSeverity.Sensitive,
            ClockNow.AddMinutes(-1),
            details: JsonSerializer.Serialize(new { idnp = TestIdnp, email = "leak@example.test" })));
        await db.SaveChangesAsync();
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AuditTimeline.Should().ContainSingle();
        var detail = result.Value.AuditTimeline[0].Detail;
        detail.Should().NotContain(TestIdnp,
            "PII (idnp) must be redacted before crossing the API boundary.");
        detail.Should().NotContain("leak@example.test",
            "PII (email) must be redacted before crossing the API boundary.");
        detail.Should().Contain("redacted",
            "the redactor replaces PII values with the literal '[redacted]'.");
    }

    /// <summary>R0701 / Test 7 — Submitted + no examiner → ["AssignExaminer"].</summary>
    [Fact]
    public async Task R0701_SuggestedActions_Submitted_NoExaminer_AssignExaminer()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted,
            assignedExaminerId: null);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedNextActions.Should().Contain("AssignExaminer");
    }

    /// <summary>R0701 / Test 8 — UnderExamination + no decision draft → ["DraftDecision"].</summary>
    [Fact]
    public async Task R0701_SuggestedActions_UnderExamination_NoDraft_DraftDecision()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.UnderExamination,
            assignedExaminerId: 2L);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedNextActions.Should().Contain("DraftDecision");
    }

    /// <summary>R0701 / Test 9 — Approved + no final attachment → ["FinalizeDecisionDocument"].</summary>
    [Fact]
    public async Task R0701_SuggestedActions_Approved_NoFinalAttachment_FinalizeDecisionDocument()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, dossierId) = await SeedApplicationAsync(db, solId, ApplicationStatus.Approved);
        // No SIGNED decision document on the dossier.
        await SeedDecisionAsync(db, dossierId, isSigned: false);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.SuggestedNextActions.Should().Contain("FinalizeDecisionDocument");
    }

    /// <summary>R0701 / Test 10 — HasUnappliedPrefill is true when R0552 reports candidates.</summary>
    [Fact]
    public async Task R0701_HasUnappliedPrefill_True_WhenCandidatesExist()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit, NewPrefillStub(fieldCount: 3));

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasUnappliedPrefill.Should().BeTrue();
    }

    /// <summary>R0701 / Test 11 — Permission denied: non-admin, non-examiner, no permission.</summary>
    [Fact]
    public async Task R0701_PermissionDenied_NonAdminNonExaminerNoPermission_Forbidden()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.UnderExamination,
            assignedExaminerId: 999L);
        var (audit, _) = NewAuditCapture();
        // Caller user id 7 — neither the assigned examiner (999) nor an admin nor permission-holder.
        var sut = NewService(db, NewCaller(7L, "cnas-user"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>R0701 / Test 12 — NationalIdHashPrefix is exactly 8 hex chars.</summary>
    [Fact]
    public async Task R0701_NationalIdHashPrefix_IsEightHexChars()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        var prefix = result.Value.Applicant.NationalIdHashPrefix;
        prefix.Should().HaveLength(8);
        prefix.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    /// <summary>R0701 / Test 13 — Counter cnas.application_processing.context_loaded increments.</summary>
    [Fact]
    public async Task R0701_Counter_ContextLoaded_Increments()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var observed = new List<long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "cnas.application_processing.context_loaded")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed.Add(value));
        listener.Start();

        var result = await sut.GetForCurrentUserAsync(appId);

        listener.RecordObservableInstruments();
        result.IsSuccess.Should().BeTrue();
        observed.Sum().Should().BeGreaterOrEqualTo(1,
            "every successful invocation must increment the counter.");
    }

    /// <summary>R0701 / Test 14 — Audit Sensitive APPLICATION.PROCESSING_CONTEXT_VIEWED row written.</summary>
    [Fact]
    public async Task R0701_AuditSensitiveRowWritten_PerCall()
    {
        var db = CreateContext();
        var (solId, _) = await SeedApplicantAsync(db);
        var (appId, _) = await SeedApplicationAsync(db, solId, ApplicationStatus.Submitted);
        var (audit, last) = NewAuditCapture();
        var sut = NewService(db, NewCaller(1L, "cnas-admin"), audit);

        var result = await sut.GetForCurrentUserAsync(appId);

        result.IsSuccess.Should().BeTrue();
        var captured = last();
        captured.Should().NotBeNull();
        captured!.Value.Code.Should().Be("APPLICATION.PROCESSING_CONTEXT_VIEWED");
        captured.Value.Severity.Should().Be(AuditSeverity.Sensitive);
        captured.Value.TargetId.Should().Be(appId);
        captured.Value.Details.Should().Contain("viewedFields",
            "the audit payload must enumerate the high-level field groups loaded.");
    }
}
