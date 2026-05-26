using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0200 / TOR CF 20.01-03, MR 012 — admin cron-schedule override DTOs. All Id
// fields are Sqid-encoded per CLAUDE.md RULE 3. Contracts MUST NOT use
// <see cref="..."/> references into Cnas.Ps.Core.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — outbound projection of a single Quartz job's
/// admin-configurable schedule. Carries the override row's Sqid id (so the admin UI
/// can address it for delete/pause), the stable job code (NOT a Sqid — job codes are
/// the public name of the job), the effective cron expression (operator-configured if
/// an override row exists, baked-in default otherwise), the paused flag, and the
/// when/who of the last edit.
/// </summary>
/// <param name="Id">Sqid-encoded override-row id; <c>null</c> when no override exists
/// and the row carries only the baked-in default for display.</param>
/// <param name="JobCode">Stable Quartz job code (e.g. <c>mpay-dispatcher</c>).</param>
/// <param name="CronExpression">Quartz cron expression currently in effect for the job.</param>
/// <param name="DefaultCronExpression">The baked-in default cron expression for the job.</param>
/// <param name="IsPaused">When <c>true</c> the job is paused at the Quartz layer.</param>
/// <param name="IsOverridden">When <c>true</c> the row reflects an operator override; when
/// <c>false</c> the row reflects the baked-in default (no DB row exists yet).</param>
/// <param name="UpdatedAtUtc">UTC timestamp of the last edit; <c>null</c> when no override exists.</param>
/// <param name="UpdatedByUserSqid">Sqid-encoded id of the operator that last edited; <c>null</c>
/// when no override exists or the row was created by a system path.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record JobScheduleOverrideDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string JobCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CronExpression,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DefaultCronExpression,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsPaused,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsOverridden,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    System.DateTime? UpdatedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? UpdatedByUserSqid);

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — input envelope for upserting a cron expression on a
/// Quartz job. The job code travels in the route segment; only the new cron value is
/// carried in the body. Validation pins a non-empty, ≤ 200-char expression that parses
/// successfully through <c>Quartz.CronExpression.IsValidExpression</c>.
/// </summary>
/// <param name="CronExpression">Quartz cron expression (sec / min / hour / day-of-month
/// / month / day-of-week [/ year]). 1..200 chars.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record CronExpressionInputDto(string CronExpression);
