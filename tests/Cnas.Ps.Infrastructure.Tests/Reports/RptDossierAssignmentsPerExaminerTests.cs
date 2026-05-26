using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6c report <c>RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER</c>
/// — per-examiner distribution of dossier assignments inside a UTC window. Each row carries
/// the examiner's local login, the number of tasks <c>Assigned</c> to them in the window,
/// the number of those tasks whose owning dossier had &gt;1 distinct assignee
/// (<c>Reassigned</c>), and the number of <c>Closed</c> tasks
/// (<see cref="WorkflowTask.CompletedAtUtc"/> non-null and inside the window).
/// </summary>
public class RptDossierAssignmentsPerExaminerTests
{
    /// <summary>Fixed UTC clock so the window anchor is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync(ClockNow.AddDays(-10));
        await harness.SeedTaskAsync(dossierId, "exam.one", ClockNow.AddDays(-5), completedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Examiner Username,Assigned,Reassigned,Closed");
    }

    /// <summary>
    /// Seeds three dossiers across two examiners. Dossier D1 changes hands (one task
    /// for exam.one, another for exam.two on the same dossier) — both rows count toward
    /// the "Reassigned" bucket. Dossier D2 has exam.one only (no reassignment). Dossier
    /// D3 is closed inside the window — counts toward "Closed" for its assignee.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_AggregatesPerExaminer()
    {
        var harness = Harness.Create();
        var d1 = await harness.SeedDossierAsync(ClockNow.AddDays(-20));
        var d2 = await harness.SeedDossierAsync(ClockNow.AddDays(-15));
        var d3 = await harness.SeedDossierAsync(ClockNow.AddDays(-10));

        // D1: two distinct assignees — both their rows mark the dossier as reassigned.
        await harness.SeedTaskAsync(d1, "exam.one", ClockNow.AddDays(-12), completedUtc: null);
        await harness.SeedTaskAsync(d1, "exam.two", ClockNow.AddDays(-8), completedUtc: null);
        // D2: only exam.one — not reassigned.
        await harness.SeedTaskAsync(d2, "exam.one", ClockNow.AddDays(-9), completedUtc: null);
        // D3: exam.two with a completed task inside the window.
        await harness.SeedTaskAsync(d3, "exam.two", ClockNow.AddDays(-7),
            completedUtc: ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // exam.one — Assigned = 2 (D1 + D2), Reassigned = 1 (D1 had >1 assignees), Closed = 0
        lines.Should().Contain("exam.one,2,1,0");
        // exam.two — Assigned = 2 (D1 + D3), Reassigned = 1 (D1 had >1 assignees), Closed = 1 (D3 completed)
        lines.Should().Contain("exam.two,2,1,1");
    }

    /// <summary>Tasks created outside the UTC window must not contribute to any row.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync(ClockNow.AddDays(-200));

        // In-window task — counted.
        await harness.SeedTaskAsync(d, "exam.in", ClockNow.AddDays(-5), completedUtc: null);
        // Out-of-window (before fromUtc) — excluded.
        await harness.SeedTaskAsync(d, "exam.out", ClockNow.AddDays(-150), completedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("exam.in");
        text.Should().NotContain("exam.out");
    }

    /// <summary>Missing window parameters must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingWindow_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds the parameters JSON for the [fromUtc, toUtc) half-open window.</summary>
    private static string BuildParams(DateTime fromUtc, DateTime toUtc)
        => $"{{ \"fromUtc\": \"{fromUtc.ToString("O", CultureInfo.InvariantCulture)}\", " +
           $"\"toUtc\": \"{toUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

    /// <summary>Reads the full text of a stream using UTF-8 with BOM detection.</summary>
    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Deterministic stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test harness composing EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>The in-memory database context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Caches user-profile ids per login.</summary>
        private readonly Dictionary<string, long> _userIds = new(StringComparer.Ordinal);

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-assign-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an open Dossier and returns its id.</summary>
        public async Task<long> SeedDossierAsync(DateTime createdUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-ASN",
                    NameRo = "Asn", DescriptionRo = "Asn",
                    FormSchemaJson = "{}", WorkflowCode = "WF",
                    MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                _passportId = passport.Id;
            }
            if (_solicitantId is null)
            {
                var s = new Solicitant
                {
                    CreatedAtUtc = ClockNow, NationalId = "2000000099999",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = createdUtc,
                SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SubmittedAtUtc = createdUtc,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = createdUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>
        /// Seeds a WorkflowTask attached to <paramref name="dossierId"/> and assigned to the
        /// user identified by <paramref name="examinerLogin"/>. Auto-creates the user on
        /// first reference.
        /// </summary>
        public async Task SeedTaskAsync(
            long dossierId,
            string examinerLogin,
            DateTime createdUtc,
            DateTime? completedUtc)
        {
            if (!_userIds.TryGetValue(examinerLogin, out var userId))
            {
                var user = new UserProfile
                {
                    CreatedAtUtc = ClockNow,
                    LocalLogin = examinerLogin,
                    DisplayName = examinerLogin,
                    IsActive = true,
                };
                Db.UserProfiles.Add(user);
                await Db.SaveChangesAsync();
                userId = user.Id;
                _userIds[examinerLogin] = userId;
            }

            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = createdUtc,
                DossierId = dossierId,
                Title = "Examine",
                Status = completedUtc is null ? WorkflowTaskStatus.InProgress : WorkflowTaskStatus.Completed,
                AssignedUserId = userId,
                CompletedAtUtc = completedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
