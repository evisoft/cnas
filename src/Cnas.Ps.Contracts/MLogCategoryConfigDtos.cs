using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — wire mirror of
/// <c>Cnas.Ps.Core.Domain.MLogSeverityFloor</c>. Captures the two thresholds
/// the admin UI exposes: "Notice and above" or "Critical only".
/// </summary>
public enum MLogSeverityFloorDto
{
    /// <summary>Mirror events at Notice severity or higher to MLog.</summary>
    Notice = 0,

    /// <summary>Mirror only Critical events to MLog.</summary>
    Critical = 1,
}

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — input envelope for upserting a MLog
/// category filter row.
/// </summary>
/// <param name="CategoryCode">SCREAMING_SNAKE_CASE category code (1-64 chars).</param>
/// <param name="DisplayName">Human-readable display name (1-256 chars).</param>
/// <param name="IsEnabled">Dual-write toggle.</param>
/// <param name="MinSeverity">Severity floor.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MLogCategoryConfigInputDto(
    string CategoryCode,
    string DisplayName,
    bool IsEnabled,
    MLogSeverityFloorDto MinSeverity);

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — read-side projection of a MLog
/// category filter row.
/// </summary>
/// <param name="Sqid">Sqid-encoded id.</param>
/// <param name="CategoryCode">Stable natural-key code.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="IsEnabled">Dual-write toggle.</param>
/// <param name="MinSeverity">Severity floor.</param>
/// <param name="IsActive">Soft-delete flag.</param>
/// <param name="UpdatedAtUtc">UTC instant of the most recent mutation.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record MLogCategoryConfigDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string CategoryCode,
    [property: SensitivityClassification(SensitivityLabel.Public)] string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Public)] bool IsEnabled,
    [property: SensitivityClassification(SensitivityLabel.Public)] MLogSeverityFloorDto MinSeverity,
    [property: SensitivityClassification(SensitivityLabel.Public)] bool IsActive,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? UpdatedAtUtc);
