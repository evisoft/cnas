# Performance operations guide

> Anchored to TOR PSR 005 (operator guidance for performance-affecting
> processes) and PSR 007 (explicit documentation of high-impact reports /
> jobs). Implementation references are file paths. Companion to
> [`performance.md`](performance.md), [`performance-kpis.md`](performance-kpis.md)
> and [`operations.md`](operations.md).

## 1. Scope

This guide tells operators which processes degrade SI „Protecția Socială"
performance and how the platform isolates them. It serves SREs, DBAs, and
CNAS ops staff. It applies any time the system is in production or staging.

## 2. Objectives

- Keep on-line transactional latency inside the PSR 001 / PSR 010
  envelope while reporting and batch workloads run (PSR 006).
- Route every long-running report off the primary database (PSR 006).
- Confine heavy back-end jobs to off-peak hours (PSR 004 / PSR 005).
- Surface queue depth and gate decisions so saturation is visible (PSR 007).
- Document explicit operator guidance for concurrent processes (PSR 005).

## 3. Implementation map

| Control | Where | Notes |
|---|---|---|
| Read-replica routing | [`Application/Abstractions/IReadOnlyCnasDbContext.cs`](../src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs) | All reporting reads go here. |
| Long-running report marker | [`Application/Reporting/LongRunningReportServiceAttribute.cs`](../src/Cnas.Ps.Application/Reporting/LongRunningReportServiceAttribute.cs) | Marker enforced by `ReadReplicaLayeringTests`. |
| Pool sizing | [`Infrastructure/Persistence/PostgresPoolOptions.cs`](../src/Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs) | `MaxPoolSize=2000` per pod in front of PgBouncer. |
| Peak-hour gate | [`Infrastructure/Scheduling/PeakHourGate.cs`](../src/Cnas.Ps.Infrastructure/Scheduling/PeakHourGate.cs) | Europe/Chisinau 22:00-06:00 default. |
| Job profile registry | [`Application/Scheduling/JobScheduleProfileRegistry.cs`](../src/Cnas.Ps.Application/Scheduling/JobScheduleProfileRegistry.cs) | Marks each Quartz job `OffPeakOnly` / `Always` / `Anytime`. |
| Admin override | [`Api/Controllers/PeakHourGateAdminController.cs`](../src/Cnas.Ps.Api/Controllers/PeakHourGateAdminController.cs) | `GET /api/admin/peak-hour-gate/status`, `POST .../override`. |
| Metrics | [`Infrastructure/Observability/CnasMeter.cs`](../src/Cnas.Ps.Infrastructure/Observability/CnasMeter.cs) | `cnas.peak_hour.gate{decision}` + report / job counters. |

## 4. Procedure

### 4.1 Routine operation

1. Long-running reports run only through services marked with
   `[LongRunningReportService]` and constructor-inject
   `IReadOnlyCnasDbContext`. The architecture test
   `LongRunningReportServicesUseReadReplica` blocks accidental drift.
2. Quartz jobs listed as `OffPeakOnly` in `JobScheduleProfileRegistry.Defaults`
   short-circuit during peak hours via `IPeakHourGate.EvaluateAsync`. Jobs
   marked `Always` (security-critical: SIEM forwarder, alert evaluator,
   session auto-lock, support-ticket SLA, system-update notification)
   fire regardless. Jobs marked `Anytime` are ungated (treasury distribution,
   admin-action backlog observer).
3. The PgBouncer-fronted Npgsql pool is sized once per pod from
   `Postgres:Pool` (see `PostgresPoolOptions`). Production must keep
   `UsePgBouncer=true`.

### 4.2 Concurrent process recommendations (PSR 005)

| Do not run concurrently | Why | Mitigation |
|---|---|---|
| Daily KPI snapshot + backup execution | Both `OffPeakOnly` and both sweep the replica heavily. | Cron offsets — KPI at 02:00, `BackupExecutionJob` at 03:00. |
| Mass recalculation apply + integrity check | Both scan large slices. | `MassRecalculationApply` configured before `IntegrityCheck`. |
| Treasury feed import + treasury distribution | Same fact tables. | Feed runs `OffPeakOnly`; distribution is `Anytime`. Operator must not manually start the import during business hours. |
| Migration DryRun + active reporting | DryRun pins replica connections. | DryRun is `OffPeakOnly`; operators must not trigger ad-hoc from admin UI during 08:00-18:00. |

### 4.3 Triage when latency spikes

1. Check `/health/ready` (see [`operations.md`](operations.md)).
2. Inspect `cnas.peak_hour.gate{decision="skip"}` — sustained skips during
   business hours are expected; sustained `allow` outside business hours
   suggests an operator override is still in effect.
3. Inspect `cnas.report_job.run` and the per-domain counters in
   `CnasMeter` (e.g., `cnas.kpi.snapshot.run`, `cnas.treasury.feed.*`).
4. If the read replica is unhealthy, `DatabaseReplicaHealthCheck` will
   surface in `/api/health/database`; the routing layer falls back to the
   primary with a WARN log entry. Reporting continues but primary load
   rises — escalate to DBAs.

## 5. Validation

- Every release runs the architecture suite which includes
  `LongRunningReportServicesUseReadReplica` and pins on the canonical
  `ReportingService` marker.
- Off-peak gating is unit-tested by `PeakHourGateTests` (wrap-around,
  override, unknown job code) and per-job by `KpiSnapshotJobTests` etc.
- Admin override audit codes (`PEAK_HOUR_GATE.OVERRIDDEN`) appear under
  `Critical` severity in `AuditLog` — verifiable via the audit search UI.
- Pool topology is documented and pinned in
  [`operations.md`](operations.md) §"Database connection pooling (R0025)".

## 6. References

- TOR PSR 004 (peak hours), PSR 005 (operator guidance), PSR 006 (KPI
  vs transactional isolation), PSR 007 (explicit doc of heavy reports).
- [`performance.md`](performance.md) — SLO declaration + k6 harness.
- [`performance-kpis.md`](performance-kpis.md) — guaranteed KPI table.
- [`operations.md`](operations.md) — pooling + health endpoints.
- Iteration notes: iter 67 (`PeakHourGate`), iter 76
  (`IntegrityCheckJob`), iter 84 (`LongRunningReportService` marker),
  iter 90 (backup jobs).
