# Feature — Background jobs & automation

## What it is

Cross-cutting work that doesn't belong in the request lane: scheduled
recomputes, dispatcher loops, sweepers, ETL projections, maintenance
windows, system-update scheduling, migration runs, backup runs, mass
recalculation. Quartz.NET as the scheduler. Every firing routes through
`FailedJobListener` for dead-letter capture.

## TOR / UC mapping

- **UC20** — Proceduri automate.
- TOR clauses: CF 20.*, PSR 006, MR 011.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/automation/run` | `CnasAdmin` | Trigger an automation manually |
| `POST /api/automation-schedules` | `CnasAdmin` | Cron schedule management |
| `POST /api/backup-admin/run` | `CnasAdmin` | Manual backup |
| `POST /api/backup-policies` | `CnasAdmin` | Configure retention |
| `POST /api/maintenance-windows` | `CnasTechAdmin` | Block scheduled jobs during maintenance |
| `POST /api/peak-hour-gate-admin` | `CnasTechAdmin` | Toggle peak-hour gating |
| `POST /api/system-update-events` | `CnasTechAdmin` | Record an applied system update |
| `POST /api/system-update-schedules` | `CnasTechAdmin` | Schedule a future update |
| `POST /api/etl-projections` | `CnasAdmin` | Trigger ETL projection rebuild |
| `POST /api/migration-admin/run` | `CnasAdmin` | Run a data-migration plan |
| `POST /api/migration-plans` | `CnasAdmin` | Manage migration plan rows |
| `POST /api/admin/failed-jobs/{id}/replay` | `CnasAdmin` | Replay a failed job |
| `POST /api/mass-recalculation` | `CnasAdmin` | Bulk recompute |

## Code map

- Controllers
  - [`AutomationController.cs`](../../src/Cnas.Ps.Api/Controllers/AutomationController.cs)
  - [`AutomationSchedulesController.cs`](../../src/Cnas.Ps.Api/Controllers/AutomationSchedulesController.cs)
  - [`BackupAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/BackupAdminController.cs)
  - [`BackupPoliciesController.cs`](../../src/Cnas.Ps.Api/Controllers/BackupPoliciesController.cs)
  - [`MaintenanceWindowsController.cs`](../../src/Cnas.Ps.Api/Controllers/MaintenanceWindowsController.cs)
  - [`PeakHourGateAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/PeakHourGateAdminController.cs)
  - [`SystemUpdateEventsController.cs`](../../src/Cnas.Ps.Api/Controllers/SystemUpdateEventsController.cs)
  - [`SystemUpdateSchedulesController.cs`](../../src/Cnas.Ps.Api/Controllers/SystemUpdateSchedulesController.cs)
  - [`EtlProjectionsController.cs`](../../src/Cnas.Ps.Api/Controllers/EtlProjectionsController.cs)
  - [`MigrationAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MigrationAdminController.cs)
  - [`MigrationPlansController.cs`](../../src/Cnas.Ps.Api/Controllers/MigrationPlansController.cs)
  - [`MassRecalculationAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MassRecalculationAdminController.cs)
  - [`AdminController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminController.cs) (failed-job replay)
- Application services
  - `IAutomationService`, `IRecurrentPaymentSchedulerService`,
    `IRecurrentPaymentAdvancer`, `IJobStateInspector`.
- Infrastructure
  - `QuartzComposition` — DI + schedule registration.
  - `MPayDispatcherJob`, `MConnectSyncJob`, `DossierSlaMonitorJob`,
    `MakerCheckerExpirySweeper`, `FailedJobListener`.

## Quartz jobs

| Job | Cadence | Purpose |
|---|---|---|
| `DossierSlaMonitorJob` | every 15 min | Flag overdue `WorkflowTask` rows + notify assignee |
| `MPayDispatcherJob` | every 5 min | Drain approved-but-not-yet-paid queue |
| `MConnectSyncJob` | daily 03:00 UTC | Refresh stale `InsuredPerson` rows from RSP |
| `MakerCheckerExpirySweeper` | every 5 min | Flip Pending → Expired on stale 4-eyes actions |

## Data model

| Entity | Purpose |
|---|---|
| `FailedJob` | Dead-letter row — failure stack, payload snapshot, replay flag. |
| `JobScheduleOverride` | Per-job schedule override (e.g. shift `MConnectSyncJob` for a maintenance window). |
| `BackupRun` + `BackupPolicy` + `BackupIntegrityCheck` | Backup management. |
| `MaintenanceWindow` | Window during which scheduled jobs are gated. |
| `SystemUpdateEvent` + `JobScheduleOverride` | System-update audit + schedule. |
| `BulkOperationRun` + `BulkSelection` | Bulk-action run history. |
| `MigrationPlan` + `MigrationRun` + `MigrationBatch` + `MigrationFinding` | Data migration. |

## Business rules

- All jobs are **idempotent** — a duplicate firing must not cause
  double-debit. Enforced through external-transaction keys
  (`MPayOrder.IdempotencyKey`), unique constraints, and
  conditional updates.
- Failed jobs persist as `FailedJob` rows for manual replay; no
  silent retries beyond Polly's per-call policy.
- Maintenance windows gate Quartz triggers at `JobListener` level —
  the window is checked before the job fires, not after.

## Tests

- `tests/Cnas.Ps.Infrastructure.Tests/Jobs/`

## What's NOT here

- Out-of-process job runner — Quartz runs in-process. Scaling out
  requires multiple API pods with the same job set; coordination
  through the Quartz Postgres cluster (configured in
  `QuartzComposition`).
