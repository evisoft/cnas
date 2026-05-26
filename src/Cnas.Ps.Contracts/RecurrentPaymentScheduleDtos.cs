using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1000..R1034 / TOR §3.2-Z — DTOs for the recurrent-payment scheduler
// driving the monthly state-support and similar monthly-allowance services.
// All Id fields are Sqid-encoded per CLAUDE.md RULE 3.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1000..R1034 — outbound projection of a single
/// <c>cnas.RecurrentPaymentSchedules</c> row.
/// </summary>
/// <param name="Id">Sqid-encoded schedule id.</param>
/// <param name="BeneficiarySqid">Sqid-encoded beneficiary id.</param>
/// <param name="ServiceCode">Stable service / passport code (e.g. <c>3.2-Z</c>).</param>
/// <param name="Amount">Per-payment amount in MDL.</param>
/// <param name="NextPaymentDate">Next due date.</param>
/// <param name="Cadence">Cadence step (stable enum-name: <c>Monthly</c> / <c>Quarterly</c> / <c>Annual</c>).</param>
/// <param name="IsActive">True when the dispatcher should process this schedule.</param>
/// <param name="LastPaymentAtUtc">UTC instant of the most recent successful dispatch.</param>
/// <param name="FailureCount">Consecutive failure count.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record RecurrentPaymentScheduleDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string BeneficiarySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ServiceCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly NextPaymentDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Cadence,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsActive,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? LastPaymentAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int FailureCount);

/// <summary>
/// R1000..R1034 — input envelope for creating a recurrent-payment schedule.
/// </summary>
/// <param name="BeneficiarySqid">Sqid-encoded beneficiary id.</param>
/// <param name="ServiceCode">Stable service / passport code (1..32 chars).</param>
/// <param name="Amount">Per-payment amount in MDL (must be &gt; 0).</param>
/// <param name="NextPaymentDate">First due date (must be in the future or today).</param>
/// <param name="Cadence">Cadence step (stable enum-name).</param>
[SensitivityClassification(SensitivityLabel.Public)]
public sealed record RecurrentPaymentScheduleCreateInputDto(
    string BeneficiarySqid,
    string ServiceCode,
    decimal Amount,
    DateOnly NextPaymentDate,
    string Cadence);

/// <summary>
/// R1000..R1034 — paged envelope returned by the schedules list endpoint.
/// </summary>
/// <param name="Items">Schedules on the requested page.</param>
/// <param name="Total">Total matching schedules across all pages.</param>
/// <param name="Skip">Page offset that was applied.</param>
/// <param name="Take">Page size that was applied.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record RecurrentPaymentSchedulePageDto(
    IReadOnlyList<RecurrentPaymentScheduleDto> Items,
    int Total,
    int Skip,
    int Take);
