using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Migration;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Migration;

/// <summary>
/// R2431 / TOR M4 — placeholder fallback mapper used when no concrete
/// <see cref="IMigrationRecordMapper"/> is registered for a plan's
/// <see cref="MigrationPlan.TargetEntityName"/>. Passes the raw source
/// fields through unchanged (JSON-serialised) and emits a single
/// <c>MAPPING.UNCUSTOMISED</c> Info finding so operators can spot
/// targets that lack a real mapper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection marker.</b> The mapper advertises
/// <see cref="TargetEntityName"/> as the wildcard <c>"*"</c> — the
/// importer's mapper registry treats this entry as the fallback when no
/// per-target mapper matches.
/// </para>
/// <para>
/// <b>Target-key derivation.</b> The mapper uses the
/// <see cref="MigrationSourceRecord.SourceFingerprint"/> as the
/// <see cref="MigrationMappedRecord.TargetEntityKey"/> — the framework
/// only needs a stable per-row key, and the fingerprint already satisfies
/// that contract.
/// </para>
/// </remarks>
public sealed class IdentityMigrationRecordMapper : IMigrationRecordMapper
{
    /// <summary>Stable wildcard marker used by the importer's mapper-registry fallback.</summary>
    public const string WildcardTargetName = "*";

    /// <summary>Stable finding code emitted to flag uncustomised passthrough mapping.</summary>
    public const string UncustomisedFindingCode = "MAPPING.UNCUSTOMISED";

    /// <summary>Cached JSON serializer options.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public string TargetEntityName => WildcardTargetName;

    /// <inheritdoc />
    public Task<Result<MigrationMappedRecord>> MapAsync(
        MigrationSourceRecord source,
        MigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);

        // Pass the field bag through verbatim so the staging row carries
        // every source column for forensic replay.
        var json = JsonSerializer.Serialize(source.Fields, CachedJsonOptions);
        const int Max = 16384;
        if (json.Length > Max)
        {
            json = json[..(Max - 3)] + "...";
        }

        var findings = new[]
        {
            new MigrationFindingRecord(
                MigrationFindingSeverity.Info,
                UncustomisedFindingCode,
                $"No customised mapper found for target '{plan.TargetEntityName}'; passing fields through unchanged."),
        };
        var record = new MigrationMappedRecord(
            TargetEntityKey: source.SourceFingerprint,
            FieldsJson: json,
            Findings: findings);
        return Task.FromResult(Result<MigrationMappedRecord>.Success(record));
    }
}
