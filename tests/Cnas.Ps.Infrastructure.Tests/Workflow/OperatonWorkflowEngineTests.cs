using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Tests.MGov;
using Cnas.Ps.Infrastructure.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="OperatonWorkflowEngine"/>. Uses the shared
/// <see cref="CapturingHandler"/> + <see cref="TestClock"/> pattern from the MGov suites so
/// no real HTTP calls are issued and every request is inspectable after the fact.
/// </summary>
public class OperatonWorkflowEngineTests
{
    private const string BaseUrl = "https://operaton.example.gov.md";

    /// <summary>
    /// Builds an <see cref="OperatonWorkflowEngine"/> wired to a <see cref="CapturingHandler"/>,
    /// returning both so tests can drive responses and inspect outgoing requests.
    /// </summary>
    private static (OperatonWorkflowEngine engine, CapturingHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? baseUrl = BaseUrl,
        string? basicUser = null,
        string? basicPassword = null)
    {
        var handler = new CapturingHandler(respond);
        var http = new HttpClient(handler);
        var opts = Options.Create(new WorkflowOptions
        {
            OperatonBaseUrl = baseUrl ?? string.Empty,
            OperatonBasicAuthUser = basicUser,
            OperatonBasicAuthPassword = basicPassword,
        });
        var engine = new OperatonWorkflowEngine(http, opts, NullLogger<OperatonWorkflowEngine>.Instance, new TestClock());
        return (engine, handler);
    }

    [Fact]
    public async Task StartProcessAsync_BaseUrlUnconfigured_ReturnsInternal_NoTraffic()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await sut.StartProcessAsync("DOSSIER_INTAKE", new Dictionary<string, object?>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Internal);
        handler.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task StartProcessAsync_HappyPath_PostsCanonicalBody_ReturnsInstance()
    {
        const string responseBody = "{\"id\":\"pi-1\",\"definitionId\":\"DOSSIER_INTAKE:3:abc\",\"ended\":false}";
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody),
        });

        var result = await sut.StartProcessAsync(
            "DOSSIER_INTAKE",
            new Dictionary<string, object?> { ["caseRef"] = "C-42" });

        result.IsSuccess.Should().BeTrue();
        result.Value.InstanceId.Should().Be("pi-1");
        result.Value.DefinitionKey.Should().Be("DOSSIER_INTAKE");
        result.Value.Status.Should().Be("Active");

        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be(
            $"{BaseUrl}/engine-rest/process-definition/key/DOSSIER_INTAKE/start");

        using var doc = JsonDocument.Parse(handler.LastBody);
        var vars = doc.RootElement.GetProperty("variables");
        vars.GetProperty("caseRef").GetProperty("value").GetString().Should().Be("C-42");
        vars.GetProperty("caseRef").GetProperty("type").GetString().Should().Be("String");
    }

    [Fact]
    public async Task StartProcessAsync_UpstreamReturns500_ReturnsWorkflowEngineFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await sut.StartProcessAsync("DOSSIER_INTAKE", new Dictionary<string, object?>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowEngineFailed);
    }

    [Fact]
    public async Task StartProcessAsync_StringVariable_EncodedAsTypeString()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"pi-1\",\"ended\":false}"),
        });

        await sut.StartProcessAsync("K", new Dictionary<string, object?> { ["s"] = "hello" });

        using var doc = JsonDocument.Parse(handler.LastBody);
        var v = doc.RootElement.GetProperty("variables").GetProperty("s");
        v.GetProperty("value").GetString().Should().Be("hello");
        v.GetProperty("type").GetString().Should().Be("String");
    }

    [Fact]
    public async Task StartProcessAsync_LongVariable_EncodedAsTypeLong()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"pi-1\",\"ended\":false}"),
        });

        await sut.StartProcessAsync("K", new Dictionary<string, object?>
        {
            ["count"] = 42L,
            ["age"] = 7,
        });

        using var doc = JsonDocument.Parse(handler.LastBody);
        var count = doc.RootElement.GetProperty("variables").GetProperty("count");
        count.GetProperty("value").GetInt64().Should().Be(42L);
        count.GetProperty("type").GetString().Should().Be("Long");

        var age = doc.RootElement.GetProperty("variables").GetProperty("age");
        age.GetProperty("value").GetInt32().Should().Be(7);
        age.GetProperty("type").GetString().Should().Be("Long");
    }

    [Fact]
    public async Task StartProcessAsync_DateTimeVariable_EncodedAsTypeDate()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"pi-1\",\"ended\":false}"),
        });

        var dt = new DateTime(2026, 5, 19, 10, 30, 45, 123, DateTimeKind.Utc);
        await sut.StartProcessAsync("K", new Dictionary<string, object?> { ["when"] = dt });

        using var doc = JsonDocument.Parse(handler.LastBody);
        var when = doc.RootElement.GetProperty("variables").GetProperty("when");
        when.GetProperty("type").GetString().Should().Be("Date");
        when.GetProperty("value").GetString().Should().StartWith("2026-05-19T10:30:45.123");
    }

    [Fact]
    public async Task CompleteTaskAsync_HappyPath_Posts204_ReturnsSuccess()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.CompleteTaskAsync(
            "task-1",
            new Dictionary<string, object?> { ["approved"] = true });

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsoluteUri.Should().Be(
            $"{BaseUrl}/engine-rest/task/task-1/complete");

        using var doc = JsonDocument.Parse(handler.LastBody);
        var approved = doc.RootElement.GetProperty("variables").GetProperty("approved");
        approved.GetProperty("value").GetBoolean().Should().BeTrue();
        approved.GetProperty("type").GetString().Should().Be("Boolean");
    }

    [Fact]
    public async Task CompleteTaskAsync_TaskNotFound_ReturnsWorkflowEngineFailed()
    {
        var (sut, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.CompleteTaskAsync("missing", new Dictionary<string, object?>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowEngineFailed);
    }

    [Fact]
    public async Task GetInstanceAsync_HappyPath_ParsesActiveTasks()
    {
        // Two-stage handler: first call returns the instance; second returns the tasks list.
        var step = 0;
        var (sut, _) = Build(req =>
        {
            step++;
            if (step == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"pi-1\",\"definitionId\":\"DOSSIER_INTAKE:2:dep\",\"ended\":false,\"suspended\":false}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"id\":\"t-1\",\"name\":\"Examine\",\"assignee\":\"examiners\",\"due\":\"2026-05-20T12:00:00.000+0000\"}]"),
            };
        });

        var result = await sut.GetInstanceAsync("pi-1");

        result.IsSuccess.Should().BeTrue();
        result.Value.InstanceId.Should().Be("pi-1");
        result.Value.DefinitionKey.Should().Be("DOSSIER_INTAKE");
        result.Value.Status.Should().Be("Active");
        result.Value.ActiveTasks.Should().HaveCount(1);
        result.Value.ActiveTasks[0].TaskId.Should().Be("t-1");
        result.Value.ActiveTasks[0].Name.Should().Be("Examine");
        result.Value.ActiveTasks[0].AssigneeGroup.Should().Be("examiners");
    }

    [Fact]
    public async Task CancelInstanceAsync_HappyPath_SendsDelete_ReturnsSuccess()
    {
        var (sut, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await sut.CancelInstanceAsync("pi-1", "withdrawn");

        result.IsSuccess.Should().BeTrue();
        handler.Last.Method.Should().Be(HttpMethod.Delete);
        handler.Last.RequestUri!.AbsoluteUri.Should().StartWith($"{BaseUrl}/engine-rest/process-instance/pi-1");
        handler.Last.RequestUri.Query.Should().Contain("reason=withdrawn");
    }

    [Fact]
    public async Task CancelInstanceAsync_BasicAuthHeader_PresentWhenConfigured()
    {
        var (sut, handler) = Build(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            basicUser: "operaton",
            basicPassword: "s3cret");

        var result = await sut.CancelInstanceAsync("pi-1", "cleanup");

        result.IsSuccess.Should().BeTrue();
        var auth = handler.Last.Headers.Authorization;
        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Basic");
        // base64("operaton:s3cret") == "b3BlcmF0b246czNjcmV0"
        auth.Parameter.Should().Be("b3BlcmF0b246czNjcmV0");
    }
}
