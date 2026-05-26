using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Workflow;

/// <summary>
/// REST adapter for the Operaton (Camunda 7-compatible) BPMN workflow engine.
/// Talks to the engine over <c>/engine-rest</c> using the variable-envelope format
/// <c>{ "value": ..., "type": "String" }</c> required by Camunda 7.
/// </summary>
/// <remarks>
/// <para>
/// This adapter intentionally surfaces only the subset of the engine API CNAS needs:
/// start a process, complete a task, query state, and cancel. Asynchronous external-task
/// fetch-and-lock is left to a future iteration once concrete worker code lands.
/// </para>
/// <para>
/// All failures are returned as <see cref="Result"/>/<see cref="Result{T}"/> — exceptions
/// from <see cref="HttpClient"/> are caught and mapped to <see cref="ErrorCodes.WorkflowEngineFailed"/>
/// so callers never have to write try/catch over engine calls (CLAUDE.md §2.1).
/// </para>
/// </remarks>
/// <param name="http">Typed <see cref="HttpClient"/> registered through <c>AddHttpClient</c>.</param>
/// <param name="options">Bound <see cref="WorkflowOptions"/> snapshot.</param>
/// <param name="logger">Structured logger; request bodies are NOT logged.</param>
/// <param name="clock">UTC time provider — never <see cref="DateTime.UtcNow"/> directly (CLAUDE.md cross-cutting).</param>
public sealed class OperatonWorkflowEngine(
    HttpClient http,
    IOptions<WorkflowOptions> options,
    ILogger<OperatonWorkflowEngine> logger,
    ICnasTimeProvider clock) : IWorkflowEngine
{
    private readonly HttpClient _http = http;
    private readonly WorkflowOptions _options = options.Value;
    private readonly ILogger<OperatonWorkflowEngine> _logger = logger;
    private readonly ICnasTimeProvider _clock = clock;

    /// <summary>
    /// JSON options shared by every outbound payload. Camunda 7 REST uses camelCase keys
    /// in its variable envelopes (<c>value</c>, <c>type</c>) and tolerates extra members.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<Result<WorkflowInstance>> StartProcessAsync(
        string processKey,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(variables);

        if (!TryGetBaseUrl(out var baseUrl))
        {
            return Result<WorkflowInstance>.Failure(ErrorCodes.Internal, "Workflow engine base URL not configured.");
        }

        var uri = $"{baseUrl}/engine-rest/process-definition/key/{Uri.EscapeDataString(processKey)}/start";
        var body = BuildVariablePayload(variables);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
            };
            DecorateAuth(request);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Operaton start-process failed for key {Key} with status {Status}.",
                    processKey, (int)response.StatusCode);
                return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
            }

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var instanceId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            var definitionKey = root.TryGetProperty("definitionId", out var defEl) && defEl.ValueKind == JsonValueKind.String
                ? ParseDefinitionKey(defEl.GetString()) : processKey;
            var ended = root.TryGetProperty("ended", out var endedEl) && endedEl.ValueKind == JsonValueKind.True;

            var status = ended ? "Completed" : "Active";
            var startedAt = _clock.UtcNow;
            var instance = new WorkflowInstance(
                instanceId,
                definitionKey,
                status,
                startedAt,
                ended ? startedAt : null,
                Array.Empty<WorkflowActiveTask>());

            return Result<WorkflowInstance>.Success(instance);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Operaton transport failure on start-process for key {Key}.", processKey);
            return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Operaton start-process timed out for key {Key}.", processKey);
            return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> CompleteTaskAsync(
        string taskId,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(variables);

        if (!TryGetBaseUrl(out var baseUrl))
        {
            return Result.Failure(ErrorCodes.Internal, "Workflow engine base URL not configured.");
        }

        var uri = $"{baseUrl}/engine-rest/task/{Uri.EscapeDataString(taskId)}/complete";
        var body = BuildVariablePayload(variables);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
            };
            DecorateAuth(request);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Operaton complete-task failed for task {TaskId} with status {Status}.",
                    taskId, (int)response.StatusCode);
                return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
            }

            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Operaton transport failure on complete-task for {TaskId}.", taskId);
            return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Operaton complete-task timed out for {TaskId}.", taskId);
            return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowInstance>> GetInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (!TryGetBaseUrl(out var baseUrl))
        {
            return Result<WorkflowInstance>.Failure(ErrorCodes.Internal, "Workflow engine base URL not configured.");
        }

        var instanceUri = $"{baseUrl}/engine-rest/process-instance/{Uri.EscapeDataString(instanceId)}";
        var tasksUri = $"{baseUrl}/engine-rest/task?processInstanceId={Uri.EscapeDataString(instanceId)}&active=true";

        try
        {
            using var instanceReq = new HttpRequestMessage(HttpMethod.Get, instanceUri);
            DecorateAuth(instanceReq);
            using var instanceResp = await _http.SendAsync(instanceReq, ct).ConfigureAwait(false);
            if (!instanceResp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Operaton get-instance failed for instance {InstanceId} with status {Status}.",
                    instanceId, (int)instanceResp.StatusCode);
                return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
            }

            var instancePayload = await instanceResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var instanceDoc = JsonDocument.Parse(instancePayload);
            var iRoot = instanceDoc.RootElement;

            var id = iRoot.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            var definitionKey = iRoot.TryGetProperty("definitionId", out var defEl) && defEl.ValueKind == JsonValueKind.String
                ? ParseDefinitionKey(defEl.GetString()) : string.Empty;
            var ended = iRoot.TryGetProperty("ended", out var endedEl) && endedEl.ValueKind == JsonValueKind.True;
            var suspended = iRoot.TryGetProperty("suspended", out var susEl) && susEl.ValueKind == JsonValueKind.True;

            var status = ended ? "Completed" : suspended ? "Suspended" : "Active";

            // Fetch active tasks separately — Camunda 7 splits process state and task list.
            var activeTasks = new List<WorkflowActiveTask>();
            if (!ended)
            {
                using var tasksReq = new HttpRequestMessage(HttpMethod.Get, tasksUri);
                DecorateAuth(tasksReq);
                using var tasksResp = await _http.SendAsync(tasksReq, ct).ConfigureAwait(false);
                if (tasksResp.IsSuccessStatusCode)
                {
                    var tasksPayload = await tasksResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var tasksDoc = JsonDocument.Parse(tasksPayload);
                    if (tasksDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tasksDoc.RootElement.EnumerateArray())
                        {
                            var taskId = t.TryGetProperty("id", out var tIdEl) ? tIdEl.GetString() ?? string.Empty : string.Empty;
                            var name = t.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() ?? string.Empty : string.Empty;
                            var group = t.TryGetProperty("assignee", out var aEl) && aEl.ValueKind == JsonValueKind.String ? aEl.GetString() ?? string.Empty : string.Empty;
                            DateTime? due = null;
                            if (t.TryGetProperty("due", out var dueEl) && dueEl.ValueKind == JsonValueKind.String
                                && DateTime.TryParse(dueEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                            {
                                due = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                            }
                            activeTasks.Add(new WorkflowActiveTask(taskId, name, group, due));
                        }
                    }
                }
            }

            var snapshot = new WorkflowInstance(
                id,
                definitionKey,
                status,
                _clock.UtcNow,
                ended ? _clock.UtcNow : null,
                activeTasks);

            return Result<WorkflowInstance>.Success(snapshot);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Operaton transport failure on get-instance for {InstanceId}.", instanceId);
            return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Operaton get-instance timed out for {InstanceId}.", instanceId);
            return Result<WorkflowInstance>.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result> CancelInstanceAsync(string instanceId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!TryGetBaseUrl(out var baseUrl))
        {
            return Result.Failure(ErrorCodes.Internal, "Workflow engine base URL not configured.");
        }

        var uri = $"{baseUrl}/engine-rest/process-instance/{Uri.EscapeDataString(instanceId)}?reason={Uri.EscapeDataString(reason)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            DecorateAuth(request);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Operaton cancel-instance failed for {InstanceId} with status {Status}.",
                    instanceId, (int)response.StatusCode);
                return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
            }

            return Result.Success();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Operaton transport failure on cancel-instance for {InstanceId}.", instanceId);
            return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Operaton cancel-instance timed out for {InstanceId}.", instanceId);
            return Result.Failure(ErrorCodes.WorkflowEngineFailed, "Upstream workflow engine call failed.");
        }
    }

    /// <summary>
    /// Builds a Camunda 7-compatible JSON payload <c>{ "variables": { name: { value, type } } }</c>
    /// from a loosely-typed dictionary, mapping each .NET runtime type to the engine variable type.
    /// </summary>
    /// <param name="variables">Caller-supplied variables, possibly empty.</param>
    /// <returns>Serialized JSON ready to be sent as the request body.</returns>
    /// <remarks>
    /// Type mapping (<see cref="WorkflowOptions"/> docs):
    /// <list type="bullet">
    ///   <item><c>string</c> → <c>String</c></item>
    ///   <item><c>long</c> / <c>int</c> → <c>Long</c></item>
    ///   <item><c>decimal</c> / <c>double</c> → <c>Double</c></item>
    ///   <item><c>bool</c> → <c>Boolean</c></item>
    ///   <item><c>DateTime</c> → <c>Date</c> (ISO 8601 with milliseconds)</item>
    ///   <item>anything else → <c>Json</c> (serialized via <see cref="JsonSerializer"/>)</item>
    /// </list>
    /// </remarks>
    private static string BuildVariablePayload(IReadOnlyDictionary<string, object?> variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("variables");
            writer.WriteStartObject();

            foreach (var kvp in variables)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartObject();
                WriteVariableValue(writer, kvp.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the <c>value</c> and <c>type</c> fields of a single variable envelope.
    /// Centralised here so the mapping table is unit-test addressable from one place.
    /// </summary>
    private static void WriteVariableValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull("value");
                writer.WriteString("type", "String");
                break;
            case string s:
                writer.WriteString("value", s);
                writer.WriteString("type", "String");
                break;
            case bool b:
                writer.WriteBoolean("value", b);
                writer.WriteString("type", "Boolean");
                break;
            case int i:
                writer.WriteNumber("value", i);
                writer.WriteString("type", "Long");
                break;
            case long l:
                writer.WriteNumber("value", l);
                writer.WriteString("type", "Long");
                break;
            case decimal m:
                writer.WriteNumber("value", (double)m);
                writer.WriteString("type", "Double");
                break;
            case double d:
                writer.WriteNumber("value", d);
                writer.WriteString("type", "Double");
                break;
            case float f:
                writer.WriteNumber("value", f);
                writer.WriteString("type", "Double");
                break;
            case DateTime dt:
                // Camunda 7 ISO-8601 with milliseconds; force UTC kind to avoid local-time leakage.
                var asUtc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                writer.WriteString("value", asUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                writer.WriteString("type", "Date");
                break;
            default:
                // Fall back to JSON-serialised payload — the engine stores it as a string and
                // declared type "Json", and the BPMN model is responsible for parsing it.
                var json = JsonSerializer.Serialize(value, JsonOptions);
                writer.WriteString("value", json);
                writer.WriteString("type", "Json");
                break;
        }
    }

    /// <summary>
    /// Adds the <c>Authorization: Basic ...</c> header if Basic credentials are configured;
    /// otherwise no authentication header is sent (suitable for dev/local Operaton).
    /// </summary>
    private void DecorateAuth(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (!string.IsNullOrEmpty(_options.OperatonBasicAuthUser))
        {
            var raw = $"{_options.OperatonBasicAuthUser}:{_options.OperatonBasicAuthPassword}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
    }

    /// <summary>
    /// Resolves the configured base URL, trimmed of trailing slashes. Returns <c>false</c>
    /// when the URL is missing, signalling callers to short-circuit to <see cref="ErrorCodes.Internal"/>
    /// without issuing any HTTP request.
    /// </summary>
    private bool TryGetBaseUrl(out string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.OperatonBaseUrl))
        {
            _logger.LogWarning("Operaton called without configured base URL — returning INTERNAL_ERROR.");
            baseUrl = string.Empty;
            return false;
        }

        baseUrl = _options.OperatonBaseUrl.TrimEnd('/');
        return true;
    }

    /// <summary>
    /// Extracts the BPMN process-definition key from Camunda's <c>definitionId</c> value, which
    /// has the shape <c>{key}:{version}:{deploymentId}</c>. Defensive against malformed input.
    /// </summary>
    private static string ParseDefinitionKey(string? definitionId)
    {
        if (string.IsNullOrEmpty(definitionId))
        {
            return string.Empty;
        }
        var colon = definitionId.IndexOf(':');
        return colon < 0 ? definitionId : definitionId.Substring(0, colon);
    }
}
