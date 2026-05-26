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
/// Integration tests for the Annex 6d report <c>RPT-EXAMINER-OUTCOMES</c> — per-examiner
/// distribution of dossier outcomes inside a UTC window. Each row carries the examiner's
/// local login and three counters: <c>Approved</c>, <c>Rejected</c>, <c>Cancelled</c>.
/// </summary>
/// <remarks>
/// "Outcome" is sourced from the owning dossier's application status at task closure
/// (<see cref="WorkflowTask.CompletedAtUtc"/>) — task completion in window. The outcome
/// label mirrors <see cref="ApplicationStatus.Approved"/>, <see cref="ApplicationStatus.Rejected"/>,
/// or anything else (Cancelled bucket — covers Withdrawn / Closed / non-decision states).
/// </remarks>
public class RptExaminerOutcomesTests
{
    /// <summary>Fixed UTC clock so window anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-EXAMINER-OUTCOMES";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync(ApplicationStatus.Approved);
        await harness.SeedTaskAsync(d, "exam.one",
            createdUtc: ClockNow.AddDays(-5), completedUtc: ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Examiner Username,Approved,Rejected,Cancelled");
    }

    /// <summary>
    /// Mixes two examiners with different outcomes. exam.one closes one Approved + one
    /// Rejected; exam.two closes one Withdrawn (→ Cancelled bucket).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_AggregatesPerExaminer()
    {
        var harness = Harness.Create();
        var d1 = await harness.SeedDossierAsync(ApplicationStatus.Approved);
        var d2 = await harness.SeedDossierAsync(ApplicationStatus.Rejected);
        var d3 = await harness.SeedDossierAsync(ApplicationStatus.Withdrawn);

        await harness.SeedTaskAsync(d1, "exam.one", ClockNow.AddDays(-7), ClockNow.AddDays(-3));
        await harness.SeedTaskAsync(d2, "exam.one", ClockNow.AddDays(-6), ClockNow.AddDays(-2));
        await harness.SeedTaskAsync(d3, "exam.two", ClockNow.AddDays(-5), ClockNow.AddDays(-1));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("exam.one,1,1,0");
        lines.Should().Contain("exam.two,0,0,1");
    }

    /// <summary>Tasks completed outside the window must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        var dIn = await harness.SeedDossierAsync(ApplicationStatus.Approved);
        var dOut = await harness.SeedDossierAsync(ApplicationStatus.Approved);

        await harness.SeedTaskAsync(dIn, "exam.in", ClockNow.AddDays(-10), ClockNow.AddDays(-5));
        await harness.SeedTaskAsync(dOut, "exam.out", ClockNow.AddDays(-200), ClockNow.AddDays(-180));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("exam.in");
        text.Should().NotContain("exam.out");
    }

    /// <summary>Tasks without a CompletedAtUtc are excluded (no outcome yet).</summary>
    [Fact]
    public async Task Execute_ExcludesUnclosedTasks()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync(ApplicationStatus.Approved);
        await harness.SeedTaskAsync(d, "exam.open", ClockNow.AddDays(-5), completedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("exam.open");
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
                .UseInMemoryDatabase($"cnas-rpt-examiner-outcomes-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Dossier whose owning application carries the supplied status.</summary>
        public async Task<long> SeedDossierAsync(ApplicationStatus finalStatus)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-EXO",
                    NameRo = "Exo", DescriptionRo = "Exo",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000066666",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-30), SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = finalStatus,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow.AddDays(-30), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow.AddDays(-30), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>
        /// Seeds a WorkflowTask attached to <paramref name="dossierId"/> assigned to the user
        /// identified by <paramref name="examinerLogin"/>. Auto-creates the user on first reference.
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
