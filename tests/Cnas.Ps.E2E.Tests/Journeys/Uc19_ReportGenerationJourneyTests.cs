using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC19 — "Generarea rapoartelor" (render half). End-to-end journey covering the
/// <c>POST /api/reports/{code}/generate</c> endpoint that materialises a report and
/// streams the rendered bytes back as a downloadable file. Drives the real
/// <c>ReportsController</c> + <c>ReportingService</c> + <c>BuildAnnex6jDataset</c> stack
/// through HTTP so the
/// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasDecider"/> gate, the
/// Sqid-free route binding, the parameter-JSON serialisation, the
/// <see cref="ExportFormat.Csv"/> renderer, and the <see cref="Microsoft.AspNetCore.Mvc.FileStreamResult"/>
/// content negotiation are all exercised at the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b>
/// <list type="bullet">
///   <item>cnas-decider — authenticated CNAS staff with the <c>cnas-decider</c> role.
///         The generate endpoint requires the
///         <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasDecider"/>
///         policy because rendered reports may aggregate PII.</item>
///   <item>cnas-user — authenticated CNAS staff with only the <c>cnas-user</c> role
///         (insufficient for the decider policy). Used for the 403 forbidden assertion.</item>
/// </list>
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>POST /api/reports/RPT-DOSSIERS-CLOSED-BY-OUTCOME/generate</c>
///         with a body whose <c>Content-Type</c> is <c>text/csv</c> per the controller's
///         <c>ContentTypeFor(ExportFormat.Csv)</c> mapping.</item>
///   <item>The CSV body starts with the canonical Annex 6j header row
///         <c>"Outcome,Count"</c> (see <see cref="Cnas.Ps.Infrastructure.Services.ReportingService"/>
///         Annex 6j fixture).</item>
///   <item>The CSV body byte length is strictly positive — the report always emits the
///         dense three-row outcome histogram even when no dossiers match the window,
///         so a zero-byte body would indicate a renderer regression.</item>
///   <item>HTTP 403 Forbidden when called by a cnas-user persona that lacks the decider
///         policy — defense in depth for the controller-level gate.</item>
///   <item>HTTP 404 Not Found when called with a typo'd report code — the service's
///         <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/> branch is mapped to
///         <see cref="Microsoft.AspNetCore.Mvc.ControllerBase.NotFound()"/> by the
///         controller.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc19_ReportGenerationJourneyTests
{
    /// <summary>The Annex 6j report code exercised by every test in this class.</summary>
    private const string ReportCode = "RPT-DOSSIERS-CLOSED-BY-OUTCOME";

    /// <summary>The canonical CSV header row Annex 6j emits.</summary>
    private const string ExpectedHeader = "Outcome,Count";

    /// <summary>Shared authenticated host fixture (xUnit collection-scoped).</summary>
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc19_ReportGenerationJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A cnas-decider generates the Annex 6j report. The response is 200 OK with a CSV
    /// content type, the canonical header row, and a non-empty body. A single approved
    /// dossier is seeded inside the window so the body contains a real data row beyond
    /// the dense outcome scaffold.
    /// </summary>
    [Fact]
    public async Task Generate_DeciderPersona_Returns200WithCsvContentAndHeader()
    {
        // Arrange — seed an approved dossier closed in window so the histogram has a
        // non-zero data point for the Approved bucket. The seed pattern mirrors
        // RptDossiersClosedByOutcomeTests so the dataset shape is identical to the
        // integration tests that already lock the column contract.
        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc.AddDays(-7);
        var toUtc = nowUtc.AddDays(1);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        await SeedClosedDossierAsync(db, hasher, ApplicationStatus.Approved, nowUtc.AddDays(-1));

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(190_001), Roles: ["cnas-decider"])));

        var body = new ReportGenerateRequest(
            Parameters: new Dictionary<string, string?>
            {
                ["fromUtc"] = fromUtc.ToString("O", CultureInfo.InvariantCulture),
                ["toUtc"] = toUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            Format: ExportFormat.Csv);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/reports/{ReportCode}/generate", body);

        // Assert — 200 with text/csv content type and a non-empty body that starts with
        // the canonical Annex 6j header row.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv",
            "the controller's ContentTypeFor(ExportFormat.Csv) maps to text/csv");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0, "the CSV body must contain at least the header row");

        // Decode with BOM-aware UTF-8 reader because Annex 6j writes UTF-8 with BOM.
        var text = DecodeUtf8WithBom(bytes);
        text.Split("\r\n").Should().NotBeEmpty();
        text.Split("\r\n")[0].Should().Be(ExpectedHeader,
            "the Annex 6j CSV header row is part of the stable report contract");
    }

    /// <summary>
    /// A cnas-user (not a decider) is rejected by the controller's policy gate with 403
    /// Forbidden. Locks the
    /// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasDecider"/> attribute
    /// — accidentally relaxing it would let lower-privileged staff export PII-bearing
    /// reports.
    /// </summary>
    [Fact]
    public async Task Generate_UserPersonaWithoutDeciderRole_Returns403()
    {
        // Arrange — authenticate with only the cnas-user role.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(190_002), Roles: ["cnas-user"])));

        var nowUtc = DateTime.UtcNow;
        var body = new ReportGenerateRequest(
            Parameters: new Dictionary<string, string?>
            {
                ["fromUtc"] = nowUtc.AddDays(-7).ToString("O", CultureInfo.InvariantCulture),
                ["toUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            Format: ExportFormat.Csv);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/reports/{ReportCode}/generate", body);

        // Assert — 403 Forbidden; the controller never runs.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A typo'd report code returns 404 Not Found. The service's
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/> branch surfaces as a
    /// <see cref="Microsoft.AspNetCore.Mvc.ControllerBase.NotFound()"/> through the
    /// controller's <c>StatusForCode</c> switch.
    /// </summary>
    [Fact]
    public async Task Generate_UnknownReportCode_Returns404()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(190_003), Roles: ["cnas-decider"])));

        var nowUtc = DateTime.UtcNow;
        var body = new ReportGenerateRequest(
            Parameters: new Dictionary<string, string?>
            {
                ["fromUtc"] = nowUtc.AddDays(-7).ToString("O", CultureInfo.InvariantCulture),
                ["toUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            Format: ExportFormat.Csv);

        // Act
        using var response = await client.PostAsJsonAsync(
            "/api/reports/RPT-DOES-NOT-EXIST/generate", body);

        // Assert — 404 Not Found; the typo'd code never reaches the materialiser.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }

    // ─────────────────────────── Helpers ───────────────────────────

    /// <summary>
    /// Seeds a closed <see cref="Dossier"/> whose underlying <see cref="ServiceApplication"/>
    /// carries the supplied <paramref name="status"/>. Follows the same pattern as the
    /// integration-suite harness in <c>RptDossiersClosedByOutcomeTests</c> — ensures a real
    /// passport, solicitant, application, and dossier row exist so the Annex 6j join
    /// produces a non-zero bucket.
    /// </summary>
    /// <param name="db">Live DbContext from the running host (so the seed survives the request scope).</param>
    /// <param name="hasher">Deterministic hasher used to populate <c>NationalIdHash</c> per SEC 035.</param>
    /// <param name="status">Application status (drives the outcome bucket).</param>
    /// <param name="closedAtUtc">UTC timestamp written to <see cref="Dossier.ClosedAtUtc"/>.</param>
    private static async Task SeedClosedDossierAsync(
        CnasDbContext db,
        IDeterministicHasher hasher,
        ApplicationStatus status,
        DateTime closedAtUtc)
    {
        var passport = new ServicePassport
        {
            Code = $"SP-UC19-{Guid.NewGuid():N}".Substring(0, 16),
            NameRo = "Serviciu E2E UC19",
            DescriptionRo = "Pașaport seed pentru jurnalul UC19.",
            WorkflowCode = "wf-e2e",
            FormSchemaJson = "{}",
            CreatedAtUtc = closedAtUtc.AddDays(-2),
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        // A valid IDNP is not required here — the report does not validate the format.
        // We use a stable test value plus the hasher to keep the shadow column in sync,
        // matching the SEC 035 contract enforced by EF model configuration.
        const string nationalId = "2000000019019";
        var solicitant = new Solicitant
        {
            NationalId = nationalId,
            NationalIdHash = hasher.ComputeHash(nationalId),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC19 Solicitant",
            CreatedAtUtc = closedAtUtc.AddDays(-2),
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();

        var application = new ServiceApplication
        {
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = status,
            FormPayloadJson = "{}",
            SubmittedAtUtc = closedAtUtc.AddDays(-1),
            ClosedAtUtc = closedAtUtc,
            CreatedAtUtc = closedAtUtc.AddDays(-1),
            IsActive = true,
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();

        db.Dossiers.Add(new Dossier
        {
            ApplicationId = application.Id,
            DossierNumber = $"UC19-D-{Guid.NewGuid():N}".Substring(0, 16),
            ClosedAtUtc = closedAtUtc,
            CreatedAtUtc = closedAtUtc.AddDays(-1),
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Decodes the supplied UTF-8 byte payload to a string, transparently consuming the
    /// leading byte-order mark Annex 6j writes (<c>0xEF 0xBB 0xBF</c>). Mirrors the
    /// <c>ReadAllText</c> helper used in <c>RptDossiersClosedByOutcomeTests</c>.
    /// </summary>
    /// <param name="bytes">Raw response body bytes.</param>
    /// <returns>The decoded text.</returns>
    private static string DecodeUtf8WithBom(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}
