using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DependencyInjection;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC17 — "Gestionez metadate și șabloane de documente" (manage metadata + document
/// templates). The functional administrator (persona <c>cnas-admin</c>) discovers the
/// catalog of compiled-in DOCX templates. End-to-end journey covering the read-only
/// half of the surface that landed in batch #94.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active — read-only half landed in batch #94.</b> The 35 (and counting) Annex 7
/// templates are still DI-baked <c>IDocxTemplate</c> singletons — there is no persistent
/// <c>DocumentTemplate</c> entity or MinIO upload path yet. This journey locks the
/// read-only enumerate / inspect surface
/// (<see cref="Cnas.Ps.Application.UseCases.ITemplateAdminService"/>) and its HTTP
/// shell (<c>TemplatesController</c>). A follow-up batch will add upload / metadata-edit
/// endpoints and the assertions sketched below will land then.
/// </para>
/// <para>
/// <b>What this journey asserts today (batch #94, read-only).</b>
/// <list type="number">
///   <item>
///     <c>GET /api/templates</c> as a <c>cnas-admin</c> returns 200 OK with a non-empty
///     list of <see cref="TemplateCatalogEntry"/>. Every row has a non-empty
///     <see cref="TemplateCatalogEntry.Code"/> and the response includes at least one
///     known kebab-case code that lives in
///     <see cref="Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate"/> (e.g.
///     <c>refuz-aplicare</c>) — locks the projection contract.
///   </item>
///   <item>
///     <c>GET /api/templates/{code}</c> round-trips: requesting a known code returns
///     200 with that entry; an unknown code returns 404.
///   </item>
///   <item>
///     <c>GET /api/templates</c> as anonymous returns 401 — the policy gate fires
///     before the controller runs.
///   </item>
///   <item>
///     <c>GET /api/templates</c> as a <c>cnas-user</c> (no admin role) returns 403 —
///     locks the <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasAdmin"/>
///     policy attribute. Without this, a regular operator would be able to enumerate the
///     catalog (which is a discovery surface, not a data export — but admin-only by
///     policy choice).
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Future batch — what will land when persistence + MinIO arrive.</b>
/// <list type="bullet">
///   <item><c>POST /api/templates</c> — multipart upload of a new <c>.docx</c> + metadata,
///         201 Created with a Sqid-encoded template id (uploaded templates DO get Sqids
///         because the id will be a sequential surrogate key — the DI-baked compile-time
///         catalog stays string-keyed for the same RULE 3 exception documented on
///         <see cref="TemplateCatalogEntry"/>).</item>
///   <item><c>PUT /api/templates/{id}</c> — metadata mutation, 204 No Content.</item>
///   <item>Audit-log <c>TEMPLATE.UPLOADED</c> + <c>TEMPLATE.METADATA_UPDATED</c> rows
///         per SEC 042.</item>
///   <item>Round-trip render of an uploaded template — the resulting DOCX has the ZIP
///         magic-byte prefix <c>0x50 0x4B</c>.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc17_TemplateAdminJourneyTests
{
    /// <summary>
    /// A known template code that ships with the DI-baked Annex 7 catalogue. Used in the
    /// "happy path" assertions below — its presence is a stable contract reference
    /// (renaming it is a breaking change per <see cref="Cnas.Ps.Infrastructure.Documents.Templates.IDocxTemplate"/>
    /// XML doc). Chosen because <c>RefuzAplicareTemplate</c> is one of the earliest
    /// templates the business signed off on; the kebab-case form matches the per-class
    /// constant <see cref="RefuzAplicareTemplate.Code"/> verbatim.
    /// </summary>
    private const string KnownTemplateCode = "refuz-aplicare";

    /// <summary>Shared authenticated host fixture (xUnit collection-scoped).</summary>
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc17_TemplateAdminJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A <c>cnas-admin</c> lists the template catalog and the response carries every
    /// DI-registered <c>IDocxTemplate</c>. Locks the wiring between the
    /// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasAdmin"/> policy
    /// gate, the controller, and the underlying
    /// <see cref="Cnas.Ps.Application.UseCases.ITemplateAdminService"/> projection.
    /// </summary>
    /// <remarks>
    /// <b>Why no hard-coded count.</b> The catalog grows with every new Annex 7 template
    /// (currently 35; round 4 is in flight). Hard-coding a number would make this test
    /// brittle against legitimate template additions. We assert <c>&gt;= 1</c> to lock
    /// the "non-empty" contract instead — the presence of <see cref="KnownTemplateCode"/>
    /// in the projected list does the real shape-validation work.
    /// </remarks>
    [Fact]
    public async Task List_AsCnasAdmin_ReturnsAllRegisteredTemplates()
    {
        // Arrange — build a cnas-admin HTTP client.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(170_001), Roles: ["cnas-admin"])));

        // Act
        using var response = await client.GetAsync("/api/templates");

        // Assert — 200 with a non-empty catalog.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var catalog = await response.Content.ReadFromJsonAsync<IReadOnlyList<TemplateCatalogEntry>>();
        catalog.Should().NotBeNull("the catalog endpoint always materialises a list");
        catalog!.Should().NotBeEmpty("the Annex 7 catalogue ships every compiled-in template");

        catalog.Should().OnlyContain(
            e => !string.IsNullOrWhiteSpace(e.Code)
                 && !string.IsNullOrWhiteSpace(e.ClrTypeFullName)
                 && !string.IsNullOrWhiteSpace(e.AssemblyName),
            "every catalog row must carry a non-empty code and implementation metadata so the picker " +
            "never renders '(empty)' and admins can navigate from the row to the source code");

        catalog.Should().Contain(e => e.Code == KnownTemplateCode,
            "the '{0}' template is part of the stable Annex 7 contract — renaming it is a breaking change",
            KnownTemplateCode);
    }

    /// <summary>
    /// Single GET round-trips: requesting a known kebab-case code returns 200 with the
    /// matching <see cref="TemplateCatalogEntry"/>. Locks the per-row projection contract.
    /// </summary>
    [Fact]
    public async Task Get_KnownCode_ReturnsEntry()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(170_002), Roles: ["cnas-admin"])));

        // Act
        using var response = await client.GetAsync($"/api/templates/{KnownTemplateCode}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var entry = await response.Content.ReadFromJsonAsync<TemplateCatalogEntry>();
        entry.Should().NotBeNull();
        entry!.Code.Should().Be(KnownTemplateCode);
        entry.ClrTypeFullName.Should().NotBeNullOrWhiteSpace();
        entry.AssemblyName.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A typo'd template code returns 404 Not Found. The service's
    /// <see cref="ErrorCodes.NotFound"/> branch surfaces as a
    /// <see cref="Microsoft.AspNetCore.Mvc.ControllerBase.NotFound()"/> through the
    /// controller's <c>StatusForCode</c> switch — mirrors <c>ReportsController</c>.
    /// </summary>
    [Fact]
    public async Task Get_UnknownCode_Returns404()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(170_003), Roles: ["cnas-admin"])));

        // Act
        using var response = await client.GetAsync("/api/templates/does-not-exist-xyz");

        // Assert — 404 Not Found; the typo'd code never matches an IDocxTemplate.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Anonymous callers receive 401. Sending the request without the
    /// <see cref="TestAuthHandler.HeaderName"/> header causes the test-auth handler to
    /// return <see cref="Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult"/>,
    /// which the <c>[Authorize(Policy = CnasAdmin)]</c> attribute escalates to a 401
    /// challenge.
    /// </summary>
    [Fact]
    public async Task List_AsAnonymous_Returns401()
    {
        // Arrange — deliberately omit the X-Test-User header.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };

        // Act
        using var response = await client.GetAsync("/api/templates");

        // Assert — 401 Unauthorized.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A <c>cnas-user</c> (lacking the admin role) is rejected by the controller's policy
    /// gate with 403 Forbidden. Locks the
    /// <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasAdmin"/> attribute
    /// — accidentally relaxing it would let lower-privileged staff enumerate the
    /// template catalog, which is intentionally restricted to functional administrators
    /// per UC17.
    /// </summary>
    [Fact]
    public async Task List_AsCnasUser_Returns403()
    {
        // Arrange — authenticate with only the cnas-user role.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(170_004), Roles: ["cnas-user"])));

        // Act
        using var response = await client.GetAsync("/api/templates");

        // Assert — 403 Forbidden; the controller never runs.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // Phase 2A — Persistence + binary upload.
    // The tests below assert the full controller → service → InMemoryFileStorage path.
    // ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DOCX MIME type. Sufficient for the in-memory storage adapter — the actual byte
    /// payload is a real RefuzAplicareTemplate render, so it carries genuine ZIP magic
    /// bytes and a structurally valid OpenXML container even though the upload service
    /// only sniffs the first four bytes.
    /// </summary>
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// Renders a tiny real DOCX via the DI-baked <see cref="RefuzAplicareTemplate"/> so
    /// the upload assertions exercise the magic-byte sniff against a genuine OpenXML
    /// container — not a fake byte sequence. Re-renders per call (cheap, in-memory).
    /// </summary>
    private byte[] BuildRealDocxBytes()
    {
        using var scope = _fixture.Services.CreateScope();
        var refuz = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IDocxTemplate>>()
            .Single(t => t.TemplateCode == RefuzAplicareTemplate.Code);

        var result = refuz.Render(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["beneficiaryIdnp"] = "2000000000007",
            ["beneficiaryFullName"] = "Test Beneficiary",
            ["dossierSqid"] = "abc12345",
            ["refuseReason"] = "Stub reason for E2E.",
            ["decisionUtc"] = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
        });
        result.IsSuccess.Should().BeTrue("RefuzAplicareTemplate.Render must succeed for the stub facts above");
        return result.Value;
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> already authenticated as the given persona
    /// against the fixture's running host. Keeps the per-test boilerplate down so each
    /// upload journey reads as just "arrange persona + payload, POST, assert".
    /// </summary>
    /// <param name="role">Role to assign — usually <c>cnas-admin</c> for the happy path.</param>
    /// <returns>Disposable HTTP client carrying the test persona header.</returns>
    private HttpClient NewAuthenticatedClient(string role, long subId)
    {
        var sqids = _fixture.Services.GetRequiredService<ISqidService>();
        var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(subId), Roles: [role])));
        return client;
    }

    /// <summary>
    /// Posts a multipart upload to <c>/api/templates</c>. Centralises the form-data
    /// shape so each upload test can focus on its assertion.
    /// </summary>
    private static async Task<HttpResponseMessage> PostUploadAsync(
        HttpClient client,
        string code,
        string name,
        byte[] fileBytes,
        string contentType = DocxContentType,
        string? description = null)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", $"{code}.docx");
        content.Add(new StringContent(code), "code");
        content.Add(new StringContent(name), "name");
        if (description is not null)
        {
            content.Add(new StringContent(description), "description");
        }
        return await client.PostAsync("/api/templates", content);
    }

    /// <summary>
    /// A <c>cnas-admin</c> uploads a valid DOCX; the response is 201 and a subsequent
    /// list call surfaces the persistent row with <c>Source: "Persistent"</c>.
    /// </summary>
    [Fact]
    public async Task Upload_ValidDocx_PersistsAndListsAlongsideBaked()
    {
        // Arrange
        using var client = NewAuthenticatedClient("cnas-admin", 170_101);
        var bytes = BuildRealDocxBytes();
        var code = $"e2e-upload-{Guid.NewGuid():N}".ToLowerInvariant()[..30];

        // Act — upload.
        using var uploadResponse = await PostUploadAsync(client, code, "E2E Upload", bytes);

        // Assert — 201 with the new entry.
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            await uploadResponse.Content.ReadAsStringAsync());
        var created = await uploadResponse.Content.ReadFromJsonAsync<TemplateCatalogEntry>();
        created.Should().NotBeNull();
        created!.Code.Should().Be(code);
        created.Source.Should().Be("Persistent");
        created.Version.Should().Be(1);
        created.Name.Should().Be("E2E Upload");
        created.ContentLength.Should().Be(bytes.LongLength);

        // Assert — subsequent list call includes the row.
        using var listResponse = await client.GetAsync("/api/templates");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var catalog = await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<TemplateCatalogEntry>>();
        catalog.Should().NotBeNull();
        var row = catalog!.SingleOrDefault(e => e.Code == code);
        row.Should().NotBeNull("the just-uploaded template must appear in the catalog");
        row!.Source.Should().Be("Persistent");
    }

    /// <summary>
    /// Uploading a non-DOCX file is rejected at the magic-byte sniff. Confirms the
    /// CLAUDE.md §5.1 magic-byte defence reaches the HTTP boundary.
    /// </summary>
    [Fact]
    public async Task Upload_NonDocxFile_Returns400()
    {
        using var client = NewAuthenticatedClient("cnas-admin", 170_102);
        // PDF magic header — wrong magic for DOCX.
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 };

        using var response = await PostUploadAsync(client, "e2e-bad", "E2E Bad", pdf);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Uploading a file larger than 5 MiB is rejected. The exact rejection mode
    /// depends on which layer trips first:
    /// <list type="bullet">
    ///   <item>The framework <see cref="Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute"/>
    ///         may abort the request mid-stream — Kestrel sends an RST and the client
    ///         observes a connection failure rather than an HTTP status.</item>
    ///   <item>The service-layer <c>MaxTemplateSize</c> check may produce a clean
    ///         400 ProblemDetails.</item>
    ///   <item>The framework may also return 413 PayloadTooLarge if it can buffer the
    ///         response.</item>
    /// </list>
    /// All three outcomes are acceptable defences — the row must NOT be persisted.
    /// The test asserts that the upload is rejected (by HTTP code or by transport
    /// error) rather than persisted.
    /// </summary>
    [Fact]
    public async Task Upload_OversizedFile_Returns400()
    {
        using var client = NewAuthenticatedClient("cnas-admin", 170_103);
        var oversized = new byte[5 * 1024 * 1024 + 1];
        // Prefix the magic bytes so the size cap is what trips the validator (rather
        // than the magic-byte sniff, which would mask the size assertion).
        oversized[0] = 0x50; oversized[1] = 0x4B; oversized[2] = 0x03; oversized[3] = 0x04;

        bool rejected;
        try
        {
            using var response = await PostUploadAsync(client, "e2e-oversized", "E2E Oversized", oversized);
            rejected =
                response.StatusCode == HttpStatusCode.BadRequest
                || response.StatusCode == HttpStatusCode.RequestEntityTooLarge;
        }
        catch (HttpRequestException)
        {
            // Kestrel may abort the connection when the request body exceeds the
            // configured limit before the framework has a chance to write a response.
            // Treat that as a successful rejection — the upload did not complete.
            rejected = true;
        }

        rejected.Should().BeTrue("oversized uploads must be rejected by some defence layer");

        // Critical assertion — the row was NOT persisted under the candidate code.
        using var list = await client.GetAsync("/api/templates/e2e-oversized");
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Uploading, then downloading the just-uploaded template returns the exact bytes
    /// posted in. Confirms the round-trip through the in-memory storage adapter.
    /// </summary>
    [Fact]
    public async Task Download_PersistedTemplate_ReturnsExactBytes()
    {
        // Arrange — upload a real DOCX.
        using var client = NewAuthenticatedClient("cnas-admin", 170_104);
        var bytes = BuildRealDocxBytes();
        var code = $"e2e-dl-{Guid.NewGuid():N}".ToLowerInvariant()[..28];
        using (var up = await PostUploadAsync(client, code, "E2E Download", bytes))
        {
            up.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Act — download.
        using var dl = await client.GetAsync($"/api/templates/{code}/download");

        // Assert — bytes match.
        dl.StatusCode.Should().Be(HttpStatusCode.OK);
        var downloaded = await dl.Content.ReadAsByteArrayAsync();
        Convert.ToHexString(SHA256.HashData(downloaded)).Should()
            .Be(Convert.ToHexString(SHA256.HashData(bytes)),
                "the downloaded bytes must hash to the same SHA-256 as the uploaded ones");
        dl.Content.Headers.ContentType?.MediaType.Should().Be(DocxContentType);
    }

    /// <summary>
    /// A <c>cnas-user</c> (lacking the admin role) cannot upload — the policy gate
    /// returns 403 before the controller runs.
    /// </summary>
    [Fact]
    public async Task Upload_AsCnasUser_Returns403()
    {
        using var client = NewAuthenticatedClient("cnas-user", 170_105);
        var bytes = BuildRealDocxBytes();

        using var response = await PostUploadAsync(client, "e2e-cnas-user", "E2E User", bytes);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Uploading the same code twice creates two rows; the first is hidden from the
    /// list (IsCurrent=false), the second is the canonical "current" row and is the
    /// one returned by GET.
    /// </summary>
    [Fact]
    public async Task Upload_SameCodeTwice_CreatesNewVersion_AndOldIsHiddenFromList()
    {
        // Arrange
        using var client = NewAuthenticatedClient("cnas-admin", 170_106);
        var bytes1 = BuildRealDocxBytes();
        var bytes2 = BuildRealDocxBytes();
        var code = $"e2e-ver-{Guid.NewGuid():N}".ToLowerInvariant()[..28];

        // Act — two uploads.
        using (var r1 = await PostUploadAsync(client, code, "E2E v1", bytes1))
        {
            r1.StatusCode.Should().Be(HttpStatusCode.Created);
            var c1 = await r1.Content.ReadFromJsonAsync<TemplateCatalogEntry>();
            c1!.Version.Should().Be(1);
        }
        using (var r2 = await PostUploadAsync(client, code, "E2E v2", bytes2))
        {
            r2.StatusCode.Should().Be(HttpStatusCode.Created);
            var c2 = await r2.Content.ReadFromJsonAsync<TemplateCatalogEntry>();
            c2!.Version.Should().Be(2);
            c2.Name.Should().Be("E2E v2");
        }

        // Assert — the list contains exactly one row for the code, version=2.
        using var listResponse = await client.GetAsync("/api/templates");
        var catalog = await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<TemplateCatalogEntry>>();
        var rows = catalog!.Where(e => e.Code == code).ToList();
        rows.Should().HaveCount(1, "the older version must be hidden by IsCurrent=false");
        rows.Single().Version.Should().Be(2);
        rows.Single().Name.Should().Be("E2E v2");
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // Phase 2B — Uploaded-template rendering.
    // The HTTP "render by code" surface is deferred to UC19+ (the catalog admin in
    // batch #96 exposes upload + download but not arbitrary "render-by-code"). The
    // test below therefore exercises the service through DI directly: upload via
    // the HTTP controller (so the persistence path is end-to-end), then resolve
    // IDocumentGenerationService from the same fixture's service provider and call
    // the phase 2B GenerateFromUploadedTemplateAsync overload.
    // ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Round-trips an operator-uploaded template through the phase 2B render path:
    /// a tiny synthesised DOCX containing <c>{{citizen}}</c> is POSTed via the
    /// admin upload surface; the persisted row is then rendered through
    /// <see cref="IDocumentGenerationService.GenerateFromUploadedTemplateAsync"/>
    /// resolved from the live fixture's DI container, and the rendered bytes are
    /// asserted to carry the substituted value (and to no longer carry the literal
    /// placeholder marker).
    /// </summary>
    /// <remarks>
    /// <b>API surface note.</b> No HTTP "render by code" endpoint exists yet — the
    /// admin controller's surface is intentionally limited to catalog / upload /
    /// download. Exposing arbitrary render-by-code over HTTP requires the
    /// authorization model that UC19+ will introduce (which actor can render which
    /// template against which data). This test therefore exercises the service
    /// directly via DI, which is sufficient to lock the renderer-substitution
    /// contract end-to-end through the production composition.
    /// </remarks>
    [Fact]
    public async Task Render_UploadedTemplate_SubstitutesPlaceholders()
    {
        // Arrange — upload a tiny DOCX with a {{citizen}} placeholder via the
        // production HTTP upload surface, so the persistence + MinIO write path is
        // genuinely exercised end-to-end.
        using var client = NewAuthenticatedClient("cnas-admin", 170_201);
        var bytes = BuildTemplateBytesWithPlaceholder("Stimate {{citizen}}, dosarul a fost examinat.");
        var code = $"e2e-render-{Guid.NewGuid():N}".ToLowerInvariant()[..30];

        using (var up = await PostUploadAsync(client, code, "E2E Render", bytes))
        {
            up.StatusCode.Should().Be(HttpStatusCode.Created,
                await up.Content.ReadAsStringAsync());
        }

        // Act — resolve the service from the running host's DI scope and render
        // the uploaded template against a dictionary with a single substitution.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var docGen = scope.ServiceProvider.GetRequiredService<IDocumentGenerationService>();

        var result = await docGen.GenerateFromUploadedTemplateAsync(
            code,
            new Dictionary<string, string> { ["citizen"] = "Ana Munteanu" });

        // Assert — render succeeded; substituted value present, marker absent.
        result.IsSuccess.Should().BeTrue(
            $"the uploaded template '{code}' must be reachable via the phase 2B fallback (code={result.ErrorCode}, msg={result.ErrorMessage})");
        var text = ReadDocxBodyText(result.Value);
        text.Should().Contain("Ana Munteanu",
            "the {{citizen}} placeholder must be substituted with the dictionary value");
        text.Should().NotContain("{{citizen}}",
            "the literal placeholder marker must not survive the render");
    }

    /// <summary>
    /// Synthesises a tiny DOCX whose body contains exactly one paragraph carrying
    /// the supplied text (which should embed at least one <c>{{key}}</c>
    /// placeholder). The result is a structurally valid OpenXML container that
    /// passes the upload surface's magic-byte + MIME validation gates.
    /// </summary>
    private static byte[] BuildTemplateBytesWithPlaceholder(string text)
    {
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(
            ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();
            var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            body.AppendChild(new Paragraph(run));
            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Re-opens the rendered DOCX and concatenates every text descendant into one
    /// string. Mirrors the helper in
    /// <c>UploadedTemplateRendererTests</c> — kept local so the journey reads
    /// end-to-end without cross-project navigation.
    /// </summary>
    private static string ReadDocxBodyText(byte[] docxBytes)
    {
        using var ms = new MemoryStream(docxBytes, writable: false);
        using var package = WordprocessingDocument.Open(ms, isEditable: false);
        var body = package.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var t in body.Descendants<Text>())
        {
            sb.Append(t.Text);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // Phase 2B — Uploaded-template rendering over HTTP.
    // The render-by-code surface lands on TemplatesController as POST /api/templates/
    // {code}/render and is exercised here through the live host. Unlike the
    // earlier DI-only test, this journey closes the loop on the HTTP boundary:
    // request shape (JSON dictionary), response shape (DOCX bytes + headers), and
    // the operator-friendly CnasUser policy (broader than the catalog routes).
    // ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// End-to-end round-trip of the phase 2B render route over HTTP:
    /// <list type="number">
    ///   <item>Upload a tiny synthesised DOCX containing <c>{{citizen}}</c> via the
    ///         admin upload surface.</item>
    ///   <item>POST <c>/api/templates/{code}/render</c> with a JSON body whose
    ///         <c>data</c> dictionary maps <c>citizen</c> → "Ion Popescu".</item>
    ///   <item>Assert the response is a valid DOCX (ZIP magic bytes
    ///         <c>50 4B 03 04</c>), carries the OpenXML MIME type, and contains the
    ///         substituted citizen name (with the literal placeholder consumed).</item>
    /// </list>
    /// Locks the controller wiring (route shape, headers, status), the
    /// <see cref="IDocumentGenerationService.GenerateFromUploadedTemplateAsync"/>
    /// dispatch, and the renderer's placeholder-substitution contract — all in one
    /// HTTP round-trip.
    /// </summary>
    [Fact]
    public async Task Render_UploadedTemplate_OverHttp_ReturnsRenderedDocx()
    {
        // Arrange — upload a tiny DOCX with a {{citizen}} placeholder. The upload
        // route requires cnas-admin; the render route only requires cnas-user, but
        // a cnas-admin satisfies CnasUser through the policy tier (CLAUDE.md §2.4
        // — higher-privileged roles transparently pass lower-policy checks), so a
        // single client carries the whole journey.
        using var client = NewAuthenticatedClient("cnas-admin", 170_301);
        var bytes = BuildTemplateBytesWithPlaceholder(
            "Stimate {{citizen}}, dosarul a fost examinat.");
        var code = $"e2e-http-render-{Guid.NewGuid():N}".ToLowerInvariant()[..34];

        using (var up = await PostUploadAsync(client, code, "E2E HTTP Render", bytes))
        {
            up.StatusCode.Should().Be(HttpStatusCode.Created,
                await up.Content.ReadAsStringAsync());
        }

        // Act — render through the HTTP route. Use a strongly-typed JSON body so
        // the System.Text.Json contract is asserted end-to-end.
        var body = new RenderUploadedTemplateRequest(
            new Dictionary<string, string> { ["citizen"] = "Ion Popescu" });
        using var renderResponse = await client.PostAsJsonAsync(
            $"/api/templates/{code}/render", body);

        // Assert — 200 with DOCX MIME, ZIP magic bytes, and the substituted name.
        renderResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await renderResponse.Content.ReadAsStringAsync());
        renderResponse.Content.Headers.ContentType?.MediaType
            .Should().Be(DocxContentType);

        var rendered = await renderResponse.Content.ReadAsByteArrayAsync();
        rendered.Length.Should().BeGreaterThan(4,
            "the rendered DOCX must carry at minimum the ZIP magic bytes plus a body");
        rendered[0].Should().Be(0x50);
        rendered[1].Should().Be(0x4B);
        rendered[2].Should().Be(0x03);
        rendered[3].Should().Be(0x04);

        var text = ReadDocxBodyText(rendered);
        text.Should().Contain("Ion Popescu",
            "the {{citizen}} placeholder must be substituted with the dictionary value");
        text.Should().NotContain("{{citizen}}",
            "the literal placeholder marker must not survive the render");
    }
}
