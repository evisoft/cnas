using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Cnas.Ps.Core.Common;
using Cnas.Ps.E2E.Tests.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC11 — "Descarcă documente". End-to-end journey covering the citizen document
/// upload/download surface. Drives the real <c>DocumentsController</c> +
/// <c>DocumentServiceImpl</c> stack through HTTP so the multipart upload pipeline,
/// the SEC 010 magic-byte sniff, and the Sqid → entity round-trip on the
/// download-URL endpoint are exercised together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Citizen / authenticated CNAS staff via <see cref="TestAuthHandler"/>
/// with the <c>cnas-user</c> role. The controller is <c>[Authorize]</c>, so any
/// authenticated principal suffices.
/// </para>
/// <para>
/// <b>Why no upload happy-path test.</b> The E2E host wires
/// <see cref="ApiHostFixture"/>'s <c>MissingMinioFileStorage</c> sentinel as the
/// <c>IFileStorage</c> implementation (MinIO is intentionally not started in tests).
/// A valid magic-byte upload passes the controller's validation but throws inside the
/// storage layer, surfacing as HTTP 500. Locking the SEC 010 reject-branch is the
/// cleaner observation: it confirms the magic-byte check itself runs before any storage
/// I/O is attempted. The upload happy-path is covered by integration tests in
/// <c>Cnas.Ps.Infrastructure.Tests</c> that substitute the storage with an in-memory fake.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 400 Bad Request when the declared MIME type is not on the supported set
///         (SEC 010 magic-byte/MIME boundary).</item>
///   <item>HTTP 404 Not Found from <c>GET /api/documents/{id}/download-url</c> for a
///         non-existent (but well-formed Sqid) document id, confirming the controller's
///         <c>NotFound()</c> branch fires when the service-layer returns its NotFound
///         result.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc11_DocumentsJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc11_DocumentsJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A citizen uploads a file claimed as <c>text/plain</c> (or any non-allowlisted
    /// MIME) and the controller rejects it with 400. Confirms the SEC 010 magic-byte
    /// guard runs BEFORE the storage call, so the rejection happens regardless of
    /// whether MinIO is wired up.
    /// </summary>
    [Fact]
    public async Task Upload_UnsupportedMime_Returns400()
    {
        // Arrange — authenticated client + a multipart body declaring text/plain.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(110_001), Roles: ["cnas-user"])));

        using var form = new MultipartFormDataContent();
        var bytes = System.Text.Encoding.ASCII.GetBytes("not a real attachment, just some text");
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        // The controller binds [IFormFile] off a part named "file" by default — match it.
        form.Add(fileContent, name: "file", fileName: "decoy.txt");

        // Act
        using var response = await client.PostAsync("/api/documents/upload", form);

        // Assert — 400 from the service's ErrorCodes.FileTypeMismatch branch (controller
        // currently maps every service failure to Problem(_, statusCode: 400)).
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Requesting a download URL for a well-formed Sqid that does not resolve to any
    /// existing document returns 404 Not Found. Locks the controller's
    /// "fail → NotFound()" branch in <c>DownloadUrlAsync</c>.
    /// </summary>
    [Fact]
    public async Task DownloadUrl_NonExistentId_Returns404()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        // A syntactically-valid Sqid that decodes to a large id we know is not seeded.
        var nonExistentSqid = sqids.Encode(987_654_321);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(110_002), Roles: ["cnas-user"])));

        // Act
        using var response = await client.GetAsync($"/api/documents/{nonExistentSqid}/download-url");

        // Assert — 404 from the controller's failure branch.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }
}
