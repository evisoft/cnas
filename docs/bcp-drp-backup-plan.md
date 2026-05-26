# Business Continuity, Disaster Recovery & Backup Plan

> Anchored to Task 6.4 → Deliverable 6.2 of the TOR §5.1 implementation
> plan. Also satisfies TOR SEC 060 (backup automation), SEC 062 / SEC 065
> (integrity on crash), SEC 063 / SEC 066 (operational restoration).
> Implementation references are file paths.

## 1. Scope

Three plans in one document:

- **BCP — Business Continuity Plan.** What stays operational when
  individual components degrade (partial outages).
- **DRP — Disaster Recovery Plan.** What happens after total facility
  loss; references [`recovery-procedures.md`](recovery-procedures.md)
  for the step-by-step restore.
- **Backup Plan.** Which backups run, where they live, how long they
  are retained, and how integrity is proven.

Audience: CNAS leadership, SREs, DBAs, auditors. Applies in production
from go-live forward.

## 2. Objectives

- Continue critical disbursements during component-level failures (BCP).
- Restore the platform within a bounded RTO / RPO after total loss (DRP).
- Prove every backup is intact via SHA-256 (Backup Plan).
- Surface every backup, integrity check, and restore action as
  Critical-severity audit rows.

## 3. Implementation map

| Plan | Capability | Where |
|---|---|---|
| BCP | Read-replica routing | [`Application/Abstractions/IReadOnlyCnasDbContext.cs`](../src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs) |
| BCP | Health probes per dependency | [`Api/Composition/ApiCompositionRoot.cs`](../src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs); see [`operations.md`](operations.md) |
| BCP | MGov resilience (retry + breaker) | [`Infrastructure/MGov/MGovResilienceOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MGovResilienceOptions.cs) |
| BCP | Risk register | `IQualityRiskService` — [`Application/ServiceManagement/IQualityRiskService.cs`](../src/Cnas.Ps.Application/ServiceManagement/IQualityRiskService.cs); `QualityRiskReviewSweepJob` for annual review |
| DRP | Recovery procedure | [`recovery-procedures.md`](recovery-procedures.md) |
| DRP | Audit chain proof | `IAuditChainVerifier` — [`Application/Audit/IAuditChainVerifier.cs`](../src/Cnas.Ps.Application/Audit/IAuditChainVerifier.cs) |
| Backup | Policy registry | `BackupPolicy` — [`Core/Domain/BackupPolicy.cs`](../src/Cnas.Ps.Core/Domain/BackupPolicy.cs) |
| Backup | Orchestrator | [`Application/Backups/IBackupOrchestrator.cs`](../src/Cnas.Ps.Application/Backups/IBackupOrchestrator.cs) |
| Backup | Run ledger | `BackupRun` — [`Core/Domain/BackupRun.cs`](../src/Cnas.Ps.Core/Domain/BackupRun.cs) |
| Backup | Integrity proof | `BackupIntegrityCheck` — [`Core/Domain/BackupIntegrityCheck.cs`](../src/Cnas.Ps.Core/Domain/BackupIntegrityCheck.cs) |
| Backup | Execution job | [`Infrastructure/Jobs/BackupExecutionJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/BackupExecutionJob.cs) (every 30 min, OffPeakOnly) |
| Backup | Retention sweep | [`Infrastructure/Jobs/BackupRetentionSweepJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/BackupRetentionSweepJob.cs) (daily 03:30 UTC, OffPeakOnly) |

## 4. Procedure

### 4.1 BCP — degradation modes

| Failure | Behaviour | Operator action |
|---|---|---|
| Read replica unreachable | `DatabaseReplicaHealthCheck` reports Unhealthy; reporting falls back to primary with WARN log. | Escalate to DBAs; reporting continues. |
| MinIO unreachable | Storage health check 503. File uploads fail; everything else continues. | Restore MinIO; retry uploads. |
| MGov MConnect down | Polly breaker opens; `MConnectFallbackInvoked` counter increments; cached responses returned where available. | Wait for MGov restoration; counters surface duration. |
| MGov MNotify down | `NotificationService` fails the dispatch; notification rows stay in `Failed`. **PARTIAL** — automatic re-dispatch fallback is not yet implemented (TODO.md). Operator manually re-triggers from admin UI. |
| API pod loss | Helm rolling-update brings another replica online; stateless. | None — Kubernetes self-heals. |
| Primary DB read-only window (e.g., PgBouncer fail-over) | Writes return 5xx; reads continue via replica. | Wait for fail-over; verify `/health/ready`. |

> **Application-level read-only mode flag.** A central "READ_ONLY_MODE"
> toggle (e.g., via AbacRule) is **NOT YET IMPLEMENTED**. The supplier
> documents this gap in the BCP and treats deployment-scale freezes as
> the interim mechanism (set API replicas to 0 in Helm). Tracked in
> [TODO.md](../TODO.md) under post-go-live hardening.

### 4.2 DRP — total facility loss

> External coordination required: CNAS authorises the DR site activation;
> DevOps provisions the replacement cluster; MGov reissues mTLS
> certificates if the certificate store is lost.

- **RTO target — ≤ 4 hours** (PLACEHOLDER — confirm with CNAS during
  stabilization). The wall-clock from incident detection to platform
  available at the DR site.
- **RPO target — ≤ 1 hour** (PLACEHOLDER — confirm with CNAS). Maximum
  data loss measured from the last successful `BackupRun`.

Step-by-step restore: see [`recovery-procedures.md`](recovery-procedures.md)
§4.3 (provision target → download payload → restore → audit chain
verify). The DR cluster must use the same `Postgres:Pool`,
`Cnas:Sqids`, `Cnas:FieldEncryption`, and `Cnas:FieldHashing` config to
preserve identifier stability and encrypted-field decryptability.

### 4.3 Backup Plan

`BackupPolicy` rows are administered by `IBackupPolicyService` (admin
endpoint `/api/admin/backups/policies`). Each policy binds:

- `PolicyCode` — stable natural key, SCREAMING_SNAKE_CASE.
- `Scope` — what is backed up (e.g., `Database`, `MinioBucket`).
- `Strategy` — `Full` / `Incremental` / `Differential`.
- `CronExpression` — Quartz cron governing when the orchestrator fires.
- `RetentionDays` — sweep boundary (default 30 days per
  `BackupPolicy.RetentionDays`).
- `TargetKind` — `InMemory` / `S3Compatible` (production swap
  TBD by DevOps).

Default `BackupPolicy` codes are seeded by operators per environment —
**there is no codebase default seed**. The recommended production set
is:

| PolicyCode | Scope | Strategy | Cron | Retention | Target |
|---|---|---|---|---|---|
| `DB_DAILY_FULL` | Database | Full | `0 0 2 * * ?` (02:00 daily) | 30 days | S3 |
| `DB_HOURLY_INCR` | Database | Incremental | `0 0 * * * ?` | 7 days | S3 |
| `MINIO_DAILY_FULL` | MinIO attachments bucket | Full | `0 30 2 * * ?` | 90 days | S3 |
| `AUDIT_WEEKLY_FULL` | Audit log | Full | `0 0 4 ? * SUN` | 365 days | S3 |

Operators may add others (e.g. quarterly cold-storage). Each policy
must be created via the admin API four-eyes flow (`IBackupPolicyService`
emits `BACKUP.POLICY_CREATED` / `POLICY_TRANSITIONED` audit rows).

Backups are uploaded via `IBackupTarget.UploadAsync`; the orchestrator
re-hashes after upload and persists the SHA-256 on the run row.
Retention sweep (`BackupRetentionSweepJob`) deletes payloads past
`RetentionDays` and emits the `BACKUP.RETENTION_SWEPT` audit.

> **PARTIAL.** `S3CompatibleBackupTarget` is currently a placeholder
> returning `BACKUP.TARGET_NOT_CONFIGURED` (deterministic failure). The
> production S3 / Azure / disk adapter is a DevOps deliverable swapped
> in via DI. See iter 90 completion note in TODO.md R2307.
> `IBackupPayloadProvider` implementations (e.g. `pg_dump`-backed
> Database provider) are likewise infrastructure-layer pluggables.

## 5. Validation

- `BackupRun` rows are queryable via
  `GET /api/admin/backups/runs?status=...`; every successful run has a
  matching `BackupIntegrityCheck` row with verdict `Passed`.
- `cnas.backup.run_completed{outcome=Succeeded}` counter must increment
  per active policy per cron window.
- `IAuditChainVerifier.VerifyAsync` runs ad-hoc and after every restore.
- Annual review of the BCP / DRP / Backup Plan is driven by
  `QualityRiskReviewSweepJob` (always-on) which surfaces overdue
  reviews on the risk register.

## 6. References

- TOR Task 6.4 → Deliverable 6.2; SEC 060, SEC 062, SEC 063, SEC 065,
  SEC 066.
- [`recovery-procedures.md`](recovery-procedures.md), [`production-deployment.md`](production-deployment.md),
  [`operations.md`](operations.md).
- Iteration notes: iter 76 (`IntegrityCheckJob`), iter 89 (migration
  registry — used in DR cluster bring-up), iter 90 (`BackupPolicy` +
  orchestrator + jobs), iter 94 (`QualityRiskRegistry`).
