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
/// Integration tests for the Annex 6f report <c>RPT-CASES-BY-AGE-GROUP</c> — count of
/// active dossiers (whose <see cref="Dossier.ClosedAtUtc"/> is null) bucketed by the
/// beneficiary's age at the supplied <c>asOfUtc</c> moment. The five age buckets are
/// fixed and emitted densely (always all five rows, zero-count buckets included). Age
/// is sourced from <see cref="InsuredPerson.BirthDate"/> joined to the dossier's
/// <see cref="Solicitant.NationalId"/>; dossiers whose beneficiary is not found in
/// <see cref="InsuredPerson"/> land in the <c>UNKNOWN</c> bucket — see report XML doc.
/// </summary>
public class RptCasesByAgeGroupTests
{
    /// <summary>Fixed UTC clock so age computations are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-CASES-BY-AGE-GROUP";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Age Group,Count");
    }

    /// <summary>
    /// Seeds one active dossier whose beneficiary falls in each of the five age buckets,
    /// plus one closed dossier (excluded). Verifies each bucket is emitted with count = 1.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_BucketsByAge()
    {
        var harness = Harness.Create();
        // Each call seeds an active dossier with a beneficiary of the supplied age in years.
        await harness.SeedDossierWithAgeAsync(ageYears: 10, dossierClosed: false);   // 0-18
        await harness.SeedDossierWithAgeAsync(ageYears: 25, dossierClosed: false);   // 19-35
        await harness.SeedDossierWithAgeAsync(ageYears: 45, dossierClosed: false);   // 36-55
        await harness.SeedDossierWithAgeAsync(ageYears: 60, dossierClosed: false);   // 56-65
        await harness.SeedDossierWithAgeAsync(ageYears: 75, dossierClosed: false);   // 66+
        // Closed dossier — must be excluded.
        await harness.SeedDossierWithAgeAsync(ageYears: 25, dossierClosed: true);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("0-18,1");
        lines.Should().Contain("19-35,1");
        lines.Should().Contain("36-55,1");
        lines.Should().Contain("56-65,1");
        lines.Should().Contain("66+,1");
    }

    /// <summary>All five buckets are always emitted, even with zero dossiers (dense histogram).</summary>
    [Fact]
    public async Task Execute_DenseHistogram_EmitsAllFiveBucketsEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("0-18,0");
        lines.Should().Contain("19-35,0");
        lines.Should().Contain("36-55,0");
        lines.Should().Contain("56-65,0");
        lines.Should().Contain("66+,0");
    }

    /// <summary>Closed dossiers must be filtered out.</summary>
    [Fact]
    public async Task Execute_ExcludesClosedDossiers()
    {
        var harness = Harness.Create();
        await harness.SeedDossierWithAgeAsync(ageYears: 25, dossierClosed: false);
        await harness.SeedDossierWithAgeAsync(ageYears: 25, dossierClosed: true);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("19-35,1");
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingAsOfUtc_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
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

        /// <summary>Monotonic counter so beneficiary IDNPs do not collide across seedings.</summary>
        private int _idnpCounter;

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-agegroup-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>
        /// Seeds an active (or closed when <paramref name="dossierClosed"/> is true) dossier
        /// whose beneficiary has the supplied age in whole years at <see cref="ClockNow"/>.
        /// The <see cref="Solicitant.NationalId"/> matches the seeded <see cref="InsuredPerson.Idnp"/>
        /// so the age join resolves to a known birth date.
        /// </summary>
        public async Task SeedDossierWithAgeAsync(int ageYears, bool dossierClosed)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-AGE",
                    NameRo = "Age", DescriptionRo = "Age",
                    FormSchemaJson = "{}", WorkflowCode = "WF",
                    MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                _passportId = passport.Id;
            }

            _idnpCounter++;
            // Deterministic 13-char IDNP per seeded beneficiary.
            var idnp = $"20000000{_idnpCounter:D5}";
            // Birth date positions ageYears years and a few days in the past so the age
            // computation produces strictly more than ageYears years of life (not exactly,
            // avoiding boundary surprises).
            var birthDate = DateOnly.FromDateTime(ClockNow.AddYears(-ageYears).AddDays(-10));

            // Populate both the plaintext NationalId/Idnp and the *Hash shadow columns.
            // The Annex 6f Cases-by-Age-Group report joins Solicitant→InsuredPerson on the
            // hash column (the plaintext is encrypted in production, different ciphertext
            // per row → join cannot match without the hash). Both sides MUST share the same
            // canonicalized hash for the join to resolve.
            var idnpHash = IdHashHelper.Hash(idnp);
            Db.Solicitants.Add(new Solicitant
            {
                CreatedAtUtc = ClockNow, NationalId = idnp, NationalIdHash = idnpHash,
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
            });
            Db.InsuredPersons.Add(new InsuredPerson
            {
                CreatedAtUtc = ClockNow, Idnp = idnp, IdnpHash = idnpHash,
                LastName = "Test", FirstName = "Person",
                BirthDate = birthDate, RegisteredAtUtc = ClockNow, IsActive = true,
            });
            await Db.SaveChangesAsync();

            // Lookup by the hash shadow column rather than the (encrypted) plaintext column.
            var solicitant = await Db.Solicitants.FirstAsync(s => s.NationalIdHash == idnpHash);
            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-30), SolicitantId = solicitant.Id,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow.AddDays(-30), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = ClockNow.AddDays(-30), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = dossierClosed ? ClockNow : null,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
