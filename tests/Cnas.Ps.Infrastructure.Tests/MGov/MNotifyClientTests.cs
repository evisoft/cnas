using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MNotifyClient"/>. Covers the real MEGA spec endpoint
/// (<c>POST /api/Notification</c>), the multi-language <c>NotificationRequest</c> body
/// shape with typed recipients and optional attachments, response parsing for
/// <c>notificationId</c>, base-URL gating, upstream-failure mapping, and the legacy
/// <c>SendAsync</c> back-compat shim that translates the original
/// (recipientIdnp, channel, templateCode, parameters) tuple into the new shape so the
/// ~30 existing consumer call sites keep compiling unchanged.
/// </summary>
public class MNotifyClientTests
{
    private const string BaseUrl = "https://mnotify.example.gov.md:8443";

    private static (MNotifyClient client, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new MGovOptions
        {
            MNotifyBaseUrl = baseUrl ?? string.Empty,
            // MNotifyBearer is deliberately not set — auth is mTLS, applied at the
            // primary handler by the DI composition root. The client itself no longer
            // sends an Authorization header.
        });
        var client = new MNotifyClient(http, opts, NullLogger<MNotifyClient>.Instance, new TestClock());
        return (client, handler);
    }

    private static NotificationRequest SampleRequest(
        IReadOnlyList<NotificationAttachment>? attachments = null,
        string? correlationId = null) =>
        new(
            Subject: new Dictionary<string, string> { ["ro"] = "Decizia este gata", ["ru"] = "Решение готово" },
            Body: new Dictionary<string, string> { ["ro"] = "Vă rugăm să verificați.", ["ru"] = "Пожалуйста, проверьте." },
            BodyShort: new Dictionary<string, string> { ["ro"] = "Decizia gata" },
            Recipients: new[] { new NotificationRecipient(NotificationRecipientType.Idnp, "2000000000000") },
            Attachments: attachments,
            CorrelationId: correlationId);

    [Fact]
    public async Task SendNotificationAsync_HappyPath_PostsToApiNotification()
    {
        // The real MEGA spec endpoint is POST /api/Notification (singular), NOT the
        // invented /api/v1/dispatch the old client used. This assertion is the entire
        // point of the protocol refactor.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-1" }),
        });

        var result = await sut.SendNotificationAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/Notification");
        handler.Last.RequestUri.AbsoluteUri.Should().NotContain("/api/v1/dispatch");
    }

    [Fact]
    public async Task SendNotificationAsync_HappyPath_BodyHasSubjectBodyRecipientsShape()
    {
        // The new body must carry multi-language subject/body dicts plus a typed
        // recipients[] array — round-trip the captured JSON to a JsonElement and
        // assert each field is present and correctly shaped.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-1" }),
        });

        await sut.SendNotificationAsync(SampleRequest());

        using var doc = JsonDocument.Parse(handler.LastBody);
        var root = doc.RootElement;

        root.GetProperty("subject").GetProperty("ro").GetString().Should().Be("Decizia este gata");
        root.GetProperty("subject").GetProperty("ru").GetString().Should().Be("Решение готово");
        root.GetProperty("body").GetProperty("ro").GetString().Should().Be("Vă rugăm să verificați.");
        root.GetProperty("bodyShort").GetProperty("ro").GetString().Should().Be("Decizia gata");

        var recipients = root.GetProperty("recipients");
        recipients.GetArrayLength().Should().Be(1);
        recipients[0].GetProperty("type").GetString().Should().Be("IDNP");
        recipients[0].GetProperty("value").GetString().Should().Be("2000000000000");
    }

    [Fact]
    public async Task SendNotificationAsync_ReturnsNotificationIdFromResponse()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-abc-123" }),
        });

        var result = await sut.SendNotificationAsync(SampleRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("N-abc-123");
    }

    [Fact]
    public async Task SendNotificationAsync_BaseUrlEmpty_ReturnsMNotifyFailedWithoutHttpCall()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.SendNotificationAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MNotifyFailed);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task SendNotificationAsync_Upstream500_ReturnsMNotifyFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.SendNotificationAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MNotifyFailed);
    }

    [Fact]
    public async Task SendNotificationAsync_AttachmentsIncluded_AppearInBody()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-1" }),
        });
        var attachments = new[]
        {
            new NotificationAttachment("decision.pdf", "JVBERi0xLjQK", "application/pdf"),
        };

        await sut.SendNotificationAsync(SampleRequest(attachments: attachments));

        using var doc = JsonDocument.Parse(handler.LastBody);
        var arr = doc.RootElement.GetProperty("attachments");
        arr.GetArrayLength().Should().Be(1);
        arr[0].GetProperty("fileName").GetString().Should().Be("decision.pdf");
        arr[0].GetProperty("contentBase64").GetString().Should().Be("JVBERi0xLjQK");
        arr[0].GetProperty("contentType").GetString().Should().Be("application/pdf");
    }

    [Fact]
    public async Task SendNotificationAsync_CorrelationIdSupplied_FlowsToHeader()
    {
        // The new request DTO carries an optional CorrelationId. When supplied, it must
        // be propagated to the X-Correlation-Id header so MEGA's audit log can trace
        // the call back to the originating CNAS dossier.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-1" }),
        });

        await sut.SendNotificationAsync(SampleRequest(correlationId: "DA-2026-1234"));

        handler.Last.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        values!.Should().Contain("DA-2026-1234");
    }

    [Fact]
    public async Task SendNotificationAsync_EmptyNotificationId_ReturnsMNotifyFailed()
    {
        // Upstream returned 200 OK but with no id — treat as failure so the caller knows
        // the notification was not actually queued downstream.
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "" }),
        });

        var result = await sut.SendNotificationAsync(SampleRequest());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MNotifyFailed);
    }

    [Fact]
    public async Task SendNotificationAsync_NoRecipients_ReturnsValidationFailed()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var emptyRequest = new NotificationRequest(
            Subject: new Dictionary<string, string> { ["ro"] = "x" },
            Body: new Dictionary<string, string> { ["ro"] = "y" },
            BodyShort: null,
            Recipients: Array.Empty<NotificationRecipient>(),
            Attachments: null,
            CorrelationId: null);

        var result = await sut.SendNotificationAsync(emptyRequest);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_LegacyShim_TranslatesIntoNotificationRequest()
    {
        // The 30+ existing consumer call sites use the legacy
        // (recipientIdnp, channel, templateCode, parameters) tuple. The shim must
        // translate this into a NotificationRequest with recipient type = IDNP and a
        // subject/body derived from the supplied parameters (with {key} substitution),
        // then route through the new endpoint.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-shim-1" }),
        });
        var legacy = new MNotifyMessage(
            "2000000000000",
            "Email",
            "GENERIC",
            new Dictionary<string, string> { ["subject"] = "Decizia gata", ["body"] = "Vă rugăm verificați." });

        var result = await sut.SendAsync(legacy);

        result.IsSuccess.Should().BeTrue();
        handler.Last.RequestUri!.AbsoluteUri.Should().Be($"{BaseUrl}/api/Notification");

        using var doc = JsonDocument.Parse(handler.LastBody);
        var root = doc.RootElement;
        root.GetProperty("recipients")[0].GetProperty("type").GetString().Should().Be("IDNP");
        root.GetProperty("recipients")[0].GetProperty("value").GetString().Should().Be("2000000000000");
        // The legacy shim copies the supplied subject + body parameters into the
        // multi-language dictionaries under the default Romanian language tag.
        root.GetProperty("subject").GetProperty("ro").GetString().Should().Be("Decizia gata");
        root.GetProperty("body").GetProperty("ro").GetString().Should().Be("Vă rugăm verificați.");
    }

    [Fact]
    public async Task SendAsync_LegacyShim_BaseUrlEmpty_ReturnsMNotifyFailed()
    {
        // The legacy shim must respect the same base-URL gating as the new method so
        // local-dev/test runs never accidentally fan out to MEGA.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");
        var legacy = new MNotifyMessage(
            "2000000000000",
            "Email",
            "GENERIC",
            new Dictionary<string, string> { ["subject"] = "x", ["body"] = "y" });

        var result = await sut.SendAsync(legacy);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MNotifyFailed);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_LegacyShim_MissingRecipient_ReturnsValidationFailed()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var legacy = new MNotifyMessage(string.Empty, "Email", "T", new Dictionary<string, string>());

        var result = await sut.SendAsync(legacy);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task SendNotificationAsync_AllRecipientTypes_SerialiseToWireValues()
    {
        // Email, IDNP, and msisdn each have a canonical wire spelling per the MEGA
        // spec — case-sensitive — and the enum mapping must match exactly.
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { notificationId = "N-1" }),
        });
        var req = new NotificationRequest(
            Subject: new Dictionary<string, string> { ["ro"] = "x" },
            Body: new Dictionary<string, string> { ["ro"] = "y" },
            BodyShort: null,
            Recipients: new[]
            {
                new NotificationRecipient(NotificationRecipientType.Email, "user@example.md"),
                new NotificationRecipient(NotificationRecipientType.Idnp, "2000000000000"),
                new NotificationRecipient(NotificationRecipientType.Msisdn, "+37368111111"),
            },
            Attachments: null,
            CorrelationId: null);

        await sut.SendNotificationAsync(req);

        using var doc = JsonDocument.Parse(handler.LastBody);
        var recips = doc.RootElement.GetProperty("recipients");
        recips[0].GetProperty("type").GetString().Should().Be("email");
        recips[1].GetProperty("type").GetString().Should().Be("IDNP");
        recips[2].GetProperty("type").GetString().Should().Be("msisdn");
    }
}
