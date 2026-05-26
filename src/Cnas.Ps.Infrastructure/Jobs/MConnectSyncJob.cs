using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Daily refresh of <see cref="InsuredPerson"/> rows against the RSP (Registrul de Stat al
/// Populației) via MConnect. Runs once per day at 03:00 UTC; picks rows whose
/// <see cref="InsuredPerson.LastRspSyncUtc"/> is null or older than 30 days and updates the
/// local cache with the latest names + deceased state.
/// </summary>
/// <remarks>
/// Per CLAUDE.md "Immutable Snapshots" we do NOT rewrite historical decisions when the
/// underlying registry changes — only the slow-changing master data on InsuredPerson is
/// refreshed. MConnect failures are tolerated: the row is left untouched and a later run
/// retries.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MConnectSyncJob(
    ICnasDbContext db,
    IMConnectClient mconnect,
    ICnasTimeProvider clock,
    ILogger<MConnectSyncJob> logger) : IJob
{
    /// <summary>Refresh cadence — rows that synced within this window are skipped.</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Maximum number of persons processed per run. Caps upstream load and keeps the run well
    /// under the 24-hour window. Bigger registries should batch across multiple runs.
    /// </summary>
    private const int BatchSize = 500;

    private readonly ICnasDbContext _db = db;
    private readonly IMConnectClient _mconnect = mconnect;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<MConnectSyncJob> _logger = logger;

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _clock.UtcNow;
        var ct = context.CancellationToken;
        var threshold = now - RefreshAfter;

        var stale = await _db.InsuredPersons
            .Where(p => p.IsActive
                        && (p.LastRspSyncUtc == null || p.LastRspSyncUtc < threshold))
            .OrderBy(p => p.LastRspSyncUtc ?? DateTime.MinValue)
            .Take(BatchSize)
            .ToListAsync(ct).ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        var refreshed = 0;
        foreach (var person in stale)
        {
            ct.ThrowIfCancellationRequested();

            var request = JsonSerializer.Serialize(new { idnp = person.Idnp });
            var result = await _mconnect
                .CallAsync("RSP.GetPerson", request, ct)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "MConnectSyncJob: RSP.GetPerson failed for IDNP={Idnp} ({ErrorCode}: {ErrorMessage}). Skipping.",
                    person.Idnp, result.ErrorCode, result.ErrorMessage);
                continue;
            }

            if (!TryApply(person, result.Value))
            {
                _logger.LogWarning(
                    "MConnectSyncJob: RSP.GetPerson returned unparseable payload for IDNP={Idnp}. Skipping.",
                    person.Idnp);
                continue;
            }

            person.LastRspSyncUtc = now;
            person.UpdatedAtUtc = now;
            refreshed++;
        }

        if (refreshed > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "MConnectSyncJob refreshed {Count} insured-person records at {NowUtc:o}.",
                refreshed, now);
        }
    }

    /// <summary>
    /// Applies a successful RSP response payload (free-form JSON) to <paramref name="person"/>.
    /// Updates the fields the registry owns (names, deceased flag) and leaves locally-curated
    /// fields alone. Returns <c>false</c> when the payload is unparseable so the caller can log.
    /// </summary>
    private static bool TryApply(InsuredPerson person, string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var root = doc.RootElement;

            // Strings — only overwrite when the upstream provides a non-null string. This
            // prevents accidental wiping when RSP returns a partial record.
            if (TryReadString(root, "lastName", out var lastName)) { person.LastName = lastName; }
            if (TryReadString(root, "firstName", out var firstName)) { person.FirstName = firstName; }
            if (TryReadString(root, "patronymic", out var patronymic)) { person.Patronymic = patronymic; }

            if (root.TryGetProperty("isDeceased", out var deceasedEl)
                && (deceasedEl.ValueKind == JsonValueKind.True || deceasedEl.ValueKind == JsonValueKind.False))
            {
                person.IsDeceased = deceasedEl.GetBoolean();
            }

            if (root.TryGetProperty("dateOfDeath", out var dod)
                && dod.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    dod.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                person.DateOfDeath = parsed;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a string property from <paramref name="root"/> when present and non-null.</summary>
    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
