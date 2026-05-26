using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Quartz <see cref="IJobListener"/> that captures every failed job execution into the
/// <see cref="FailedJob"/> dead-letter queue (CLAUDE.md §6.2 — background jobs must be
/// "monitored, retryable, logged"). Operates passively: the three production jobs do
/// not depend on the listener and continue to throw on irrecoverable errors as they
/// always did.
/// </summary>
/// <remarks>
/// <para>
/// PII discipline: Quartz <c>JobDataMap</c> is a free-form key-value bag and the three
/// CNAS jobs all happen to be parameter-less today. To future-proof, the listener
/// scrubs any key whose name contains <c>idnp</c>, <c>pin</c>, <c>password</c>,
/// <c>token</c>, <c>secret</c>, or <c>key</c> (case-insensitive) before serialising the
/// remaining payload to JSON. Stack traces are truncated to 16 000 chars and exception
/// messages to 4 000 chars so a single pathological failure cannot bloat the DLQ.
/// </para>
/// <para>
/// Logging discipline: at <c>Warning</c> level the listener logs only the metadata
/// (job name, exception type, refire count). The full stack trace lives only on the
/// DLQ row, behind an admin-authorized query endpoint — never at <c>Information</c>
/// level on the application logger, where it could leak into a Serilog file sink.
/// </para>
/// </remarks>
public sealed class FailedJobListener(
    IFailedJobStore store,
    ICnasTimeProvider clock,
    ILogger<FailedJobListener> logger) : IJobListener
{
    /// <summary>Maximum stack-trace length persisted on a DLQ row.</summary>
    internal const int MaxStackTraceLength = 16_000;

    /// <summary>Maximum exception-message length persisted on a DLQ row.</summary>
    internal const int MaxExceptionMessageLength = 4_000;

    /// <summary>
    /// Case-insensitive substrings that, when matched in a <c>JobDataMap</c> key, cause
    /// the value to be replaced by <see cref="RedactedMarker"/> before serialisation.
    /// Kept conservative — when in doubt, redact.
    /// </summary>
    internal static readonly string[] SensitiveKeyTokens =
    [
        "idnp", "pin", "password", "token", "secret", "key",
    ];

    /// <summary>Literal text written in place of any value whose key matches a sensitive token.</summary>
    internal const string RedactedMarker = "<redacted>";

    private readonly IFailedJobStore _store = store;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<FailedJobListener> _logger = logger;

    /// <inheritdoc />
    public string Name => "cnas-failed-job-listener";

    /// <inheritdoc />
    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task JobWasExecuted(
        IJobExecutionContext context,
        JobExecutionException? jobException,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (jobException is null)
        {
            // Success path — nothing to record. The vast majority of fires take this branch.
            return;
        }

        // Reach for the innermost domain exception when Quartz has wrapped it. The
        // listener stores the "real" cause so operators see HttpRequestException rather
        // than the always-uniform Quartz envelope.
        var inner = (Exception?)jobException.InnerException ?? jobException;

        var jobKey = context.JobDetail.Key;
        var entry = new FailedJob
        {
            CreatedAtUtc = _clock.UtcNow,
            FailedAtUtc = _clock.UtcNow,
            JobName = jobKey.Name,
            JobGroup = jobKey.Group,
            ExceptionType = inner.GetType().FullName ?? inner.GetType().Name,
            ExceptionMessage = Truncate(inner.Message ?? string.Empty, MaxExceptionMessageLength) ?? string.Empty,
            StackTrace = Truncate(inner.StackTrace, MaxStackTraceLength),
            JobDataJson = SerializeMergedJobData(context.MergedJobDataMap),
            RefireCount = context.RefireCount,
            IsActive = true,
        };

        try
        {
            await _store.RecordFailureAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception persistEx) when (persistEx is not OperationCanceledException)
        {
            // If the DLQ itself fails to persist we must not crash Quartz — the original
            // job is already lost; swallowing here keeps the scheduler alive. The metadata
            // is logged so operators can still piece together the failure.
            _logger.LogError(
                persistEx,
                "FailedJobListener could not persist DLQ entry for JobName={JobName} (ExceptionType={ExceptionType}).",
                jobKey.Name, entry.ExceptionType);
            return;
        }

        // Warning-level — bounded metadata only, no stack trace.
        _logger.LogWarning(
            "FailedJob captured for JobName={JobName} ExceptionType={ExceptionType} RefireCount={RefireCount}.",
            jobKey.Name, entry.ExceptionType, entry.RefireCount);
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> code
    /// units. Returns the input unchanged when already within budget; preserves null
    /// inputs so callers can keep <c>StackTrace</c> nullable.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    /// <summary>
    /// Renders the supplied <see cref="JobDataMap"/> to a redacted JSON object. Returns
    /// <c>null</c> when the map is empty so the DLQ column stays NULL for the typical
    /// parameter-less job fires used today.
    /// </summary>
    /// <remarks>
    /// Only the top-level key names are inspected for the sensitive-token list — nested
    /// JSON structures inside a single value are emitted verbatim. The three production
    /// CNAS jobs (DossierSlaMonitor, MPayDispatcher, MConnectSync) take no parameters so
    /// today this method almost always returns <c>null</c>; the policy is in place so
    /// that future jobs which DO carry data (e.g. a back-fill job keyed on IDNP) cannot
    /// leak PII into the DLQ without an explicit opt-out.
    /// </remarks>
    internal static string? SerializeMergedJobData(JobDataMap map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }

        // The relaxed encoder keeps the redaction marker (`<redacted>`) readable in the
        // stored JSON instead of escaping `<` and `>` to `<`/`>`. Output is
        // still valid JSON — only the HTML-safe encoding is relaxed.
        var encoderOptions = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, encoderOptions))
        {
            writer.WriteStartObject();
            foreach (var kvp in map)
            {
                writer.WritePropertyName(kvp.Key);
                if (IsSensitiveKey(kvp.Key))
                {
                    writer.WriteStringValue(RedactedMarker);
                    continue;
                }

                // Best-effort scalar serialisation. We render most types as their string
                // representation rather than spelunking into reference types, both because
                // Quartz job data is meant to be primitive and because deep traversal
                // increases the risk of inadvertently dumping a secret hidden behind a
                // benign-looking parent key.
                switch (kvp.Value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case string s:
                        writer.WriteStringValue(s);
                        break;
                    case bool b:
                        writer.WriteBooleanValue(b);
                        break;
                    case int i:
                        writer.WriteNumberValue(i);
                        break;
                    case long l:
                        writer.WriteNumberValue(l);
                        break;
                    case double d:
                        writer.WriteNumberValue(d);
                        break;
                    case decimal m:
                        writer.WriteNumberValue(m);
                        break;
                    default:
                        writer.WriteStringValue(kvp.Value.ToString() ?? string.Empty);
                        break;
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Tests whether <paramref name="key"/> contains any of the configured
    /// <see cref="SensitiveKeyTokens"/> substrings, case-insensitively. The match is
    /// substring-based so derived names ("userIdnp", "rspToken", "encryptionKeyId")
    /// are caught alongside the bare token name.
    /// </summary>
    internal static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        foreach (var token in SensitiveKeyTokens)
        {
            if (key.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
