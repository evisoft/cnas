using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R2307 / TOR SEC 060 — DTOs for the backup-orchestration framework: policy
// registry, run ledger, integrity checks. All Id fields are Sqid-encoded
// per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>R2307 / TOR SEC 060 — outbound projection of a backup policy.</summary>
/// <param name="Id">Sqid-encoded policy id.</param>
/// <param name="PolicyCode">Stable SCREAMING_SNAKE_CASE policy code.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="Scope">Stable enum-name of the data-set scope.</param>
/// <param name="Strategy">Stable enum-name of the backup strategy.</param>
/// <param name="CronSchedule">Quartz cron expression governing fire timing.</param>
/// <param name="RetentionDays">Days backups stay before retention purge.</param>
/// <param name="TargetKind">Stable enum-name of the target adapter.</param>
/// <param name="TargetReference">Opaque target reference (bucket / path); never carries credentials.</param>
/// <param name="IsActive">True when the orchestrator should fire this policy.</param>
/// <param name="LastSuccessfulRunAt">UTC instant of the most recent Succeeded run; null until first success.</param>
/// <param name="LastFailedRunAt">UTC instant of the most recent failed run; null when no failure observed.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupPolicyDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PolicyCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Scope,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Strategy,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string CronSchedule,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int RetentionDays,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TargetKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? TargetReference,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? LastSuccessfulRunAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? LastFailedRunAt);

/// <summary>R2307 / TOR SEC 060 — input envelope for creating a backup policy.</summary>
/// <param name="PolicyCode">Stable SCREAMING_SNAKE_CASE policy code.</param>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description (≤ 2000 chars).</param>
/// <param name="Scope">Stable enum-name string of the scope.</param>
/// <param name="Strategy">Stable enum-name string of the strategy.</param>
/// <param name="CronSchedule">Quartz cron expression (≤ 64 chars).</param>
/// <param name="RetentionDays">Retention days (1..3650).</param>
/// <param name="TargetKind">Stable enum-name string of the target adapter.</param>
/// <param name="TargetReference">Opaque target reference (≤ 256 chars); never carries credentials.</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BackupPolicyCreateInputDto(
    string PolicyCode,
    string DisplayName,
    string? Description,
    string Scope,
    string Strategy,
    string CronSchedule,
    int RetentionDays,
    string TargetKind,
    string? TargetReference);

/// <summary>R2307 / TOR SEC 060 — input envelope for modifying an existing policy.</summary>
/// <param name="DisplayName">Display name (3..256 chars).</param>
/// <param name="Description">Optional free-form description.</param>
/// <param name="CronSchedule">Quartz cron expression.</param>
/// <param name="RetentionDays">Retention days (1..3650).</param>
/// <param name="TargetReference">Opaque target reference.</param>
/// <param name="ChangeReason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BackupPolicyModifyInputDto(
    string? DisplayName,
    string? Description,
    string? CronSchedule,
    int? RetentionDays,
    string? TargetReference,
    string ChangeReason);

/// <summary>R2307 / TOR SEC 060 — input envelope for reason-bearing transitions.</summary>
/// <param name="Reason">Free-form reason (3..1000 chars).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupPolicyReasonInputDto(string Reason);

/// <summary>R2307 / TOR SEC 060 — filter envelope for the policies list endpoint.</summary>
/// <param name="IsActive">Optional IsActive filter.</param>
/// <param name="Scope">Optional scope filter (stable enum-name).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BackupPolicyFilterDto(
    bool? IsActive = null,
    string? Scope = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2307 / TOR SEC 060 — paged envelope returned by the policies list endpoint.</summary>
/// <param name="Items">Policies on the requested page.</param>
/// <param name="Total">Total matching policies across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupPolicyPageDto(
    IReadOnlyList<BackupPolicyDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2307 / TOR SEC 060 — outbound projection of a backup run.</summary>
/// <param name="Id">Sqid-encoded run id.</param>
/// <param name="PolicySqid">Sqid-encoded parent policy id.</param>
/// <param name="RunNumber">Deterministic BKR-{year}-{seq} run number.</param>
/// <param name="Status">Stable enum-name of the run status.</param>
/// <param name="TriggerKind">Stable enum-name of the trigger origin.</param>
/// <param name="StartedAt">UTC instant the run started.</param>
/// <param name="CompletedAt">UTC instant the run reached a terminal status; null while in flight.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="PayloadSizeBytes">Size of the uploaded payload.</param>
/// <param name="PayloadHashSha256">Lowercase-hex SHA-256 of the payload.</param>
/// <param name="FailureReason">Sanitised PII-free failure reason; null on success.</param>
/// <param name="RetentionPurgedAt">UTC instant the retention sweep purged this run's payload.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupRunDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PolicySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RunNumber,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long? DurationMs,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long? PayloadSizeBytes,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? PayloadHashSha256,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? RetentionPurgedAt);

/// <summary>R2307 / TOR SEC 060 — filter envelope for the runs list endpoint.</summary>
/// <param name="PolicySqid">Optional Sqid-encoded policy filter.</param>
/// <param name="Status">Optional status filter (stable enum-name).</param>
/// <param name="TriggerKind">Optional trigger-kind filter (stable enum-name).</param>
/// <param name="StartedAfter">Optional lower-bound on StartedAt (inclusive, UTC).</param>
/// <param name="Skip">Page offset (default 0; must be ≥ 0).</param>
/// <param name="Take">Page size (default 50; max 100).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record BackupRunFilterDto(
    string? PolicySqid = null,
    string? Status = null,
    string? TriggerKind = null,
    DateTime? StartedAfter = null,
    int Skip = 0,
    int Take = 50);

/// <summary>R2307 / TOR SEC 060 — paged envelope returned by the runs list endpoint.</summary>
/// <param name="Items">Runs on the requested page.</param>
/// <param name="Total">Total matching runs.</param>
/// <param name="Skip">Page offset applied.</param>
/// <param name="Take">Page size applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupRunPageDto(
    IReadOnlyList<BackupRunDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>R2307 / TOR SEC 060 — outbound projection of a backup integrity check.</summary>
/// <param name="Id">Sqid-encoded check id.</param>
/// <param name="RunSqid">Sqid-encoded parent run id.</param>
/// <param name="Status">Stable enum-name of the check status.</param>
/// <param name="CheckedAt">UTC instant the check completed.</param>
/// <param name="ExpectedHash">Lowercase-hex SHA-256 expected.</param>
/// <param name="ActualHash">Lowercase-hex SHA-256 observed on re-download.</param>
/// <param name="FailureReason">Sanitised PII-free failure reason; null on Passed.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record BackupIntegrityCheckDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CheckedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ExpectedHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ActualHash,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason);
