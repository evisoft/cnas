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
/// Integration tests for the Annex 6c report <c>RPT-APPEAL-INBOX</c> — inbox of currently
/// open contestații (appeals). In the absence of a dedicated Appeal entity, appeals are
/// sourced from <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.Title"/>
/// begins with <c>APPEAL:</c>. An appeal is "open" when <see cref="WorkflowTask.CompletedAtUtc"/>
/// is <see langword="null"/>. Each row carries the appeal sqid, parent dossier sqid, the
/// beneficiary's IDNP, the moment the appeal was filed (UTC), and its age in whole days.
/// </summary>
public class RptAppealInboxTests
{
    /// <summary>Fixed UTC clock so age-day calculations are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-APPEAL-INBOX";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedAppealAsync("2000000000001", filedUtc: ClockNow.AddDays(-3), resolvedUtc: null);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Appeal Sqid,Dossier Sqid,Beneficiary IDNP,Filed (UTC),Age Days");
    }

    /// <summary>
    /// Seeds three rows — one open appeal (must appear), one resolved appeal
    /// (<see cref="WorkflowTask.CompletedAtUtc"/> non-null — must be excluded), and one
    /// open non-APPEAL workflow task (must also be excluded).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsOnlyOpenAppeals()
    {
        var harness = Harness.Create();
        await harness.SeedAppealAsync("2000000000010", filedUtc: ClockNow.AddDays(-7), resolvedUtc: null);
        await harness.SeedAppealAsync("2000000000020", filedUtc: ClockNow.AddDays(-9),
            resolvedUtc: ClockNow.AddDays(-2));
        await harness.SeedRawTaskAsync("Examine docs", filedUtc: ClockNow.AddDays(-1), resolvedUtc: null);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("2000000000010", "open appeals must surface in the inbox");
        text.Should().NotContain("2000000000020", "resolved appeals must be excluded");
        text.Should().NotContain("Examine docs", "non-APPEAL workflow tasks must be excluded");
    }

    /// <summary>Resolved appeals (CompletedAtUtc non-null) must not appear.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesResolved()
    {
        var harness = Harness.Create();
        await harness.SeedAppealAsync("2000000000030", filedUtc: ClockNow.AddDays(-5), resolvedUtc: null);
        await harness.SeedAppealAsync("2000000000031", filedUtc: ClockNow.AddDays(-5),
            resolvedUtc: ClockNow.AddDays(-1));

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("2000000000030");
        text.Should().NotContain("2000000000031");
    }

    /// <summary>Both <c>AppealSqid</c> and <c>DossierSqid</c> are Sqid-encoded — never raw longs.</summary>
    [Fact]
    public async Task Execute_SqidColumnsAreEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedAppealAsync("2000000000040", filedUtc: ClockNow.AddDays(-1), resolvedUtc: null);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var dataLine = text.Split("\r\n").First(l => l.Contains("2000000000040"));
        var parts = dataLine.Split(',');
        parts[0].Should().StartWith("sqid-");
        long.TryParse(parts[0], out _).Should().BeFalse();
        parts[1].Should().StartWith("sqid-");
        long.TryParse(parts[1], out _).Should().BeFalse();
    }

    // ─────────────────────── Helpers ───────────────────────

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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-appeal-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Dossier + an APPEAL-titled WorkflowTask referencing it.</summary>
        public Task SeedAppealAsync(string beneficiaryIdnp, DateTime filedUtc, DateTime? resolvedUtc)
            => SeedTaskAsync($"APPEAL:{beneficiaryIdnp}", beneficiaryIdnp, filedUtc, resolvedUtc);

        /// <summary>Seeds a non-APPEAL WorkflowTask used to prove the title filter.</summary>
        public Task SeedRawTaskAsync(string title, DateTime filedUtc, DateTime? resolvedUtc)
            => SeedTaskAsync(title, "2000000099999", filedUtc, resolvedUtc);

        /// <summary>Shared seed for both APPEAL and non-APPEAL tasks.</summary>
        private async Task SeedTaskAsync(string title, string beneficiaryIdnp, DateTime filedUtc, DateTime? resolvedUtc)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = filedUtc,
                Code = "SP-APL",
                NameRo = "Apl", DescriptionRo = "Apl",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = filedUtc, NationalId = beneficiaryIdnp,
                Kind = ApplicantKind.NaturalPerson, DisplayName = $"Appellant {beneficiaryIdnp}", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = filedUtc, SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = filedUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = filedUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = filedUtc,
                DossierId = dossier.Id,
                Title = title,
                Status = resolvedUtc is null ? WorkflowTaskStatus.InProgress : WorkflowTaskStatus.Completed,
                CompletedAtUtc = resolvedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
