# Recovery procedures

> Anchored to TOR SEC 063 / SEC 066 (operational restoration of availability
> and accessibility during continuity incidents). Implementation references
> are file paths. Companion to [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md).

## 1. Scope

This runbook covers operational recovery from data loss, audit-chain
break, or backup-target corruption. It targets on-call DBAs and SREs. It
applies the moment any backup integrity check fails or the primary
database becomes unrecoverable.

## 2. Objectives

- Detect data integrity loss within one hour of occurrence (SEC 062).
- Restore an authoritative backup with verifiable SHA-256 hash (SEC 060
  + SEC 063).
- Replay or verify the audit hash chain end-to-end (SEC 047 / SEC 066).
- Record every step as a Critical-severity audit row.

> **RTO / RPO targets — PLACEHOLDER.** The TOR text references operative
> restoration without binding a numeric RTO / RPO. The operational
> defaults assumed here are **RTO ≤ 4 hours**, **RPO ≤ 1 hour**. These
> values are confirmed in [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md)
> and must be re-validated with CNAS during stabilization.

## 3. Implementation map

| Step | Surface | Where |
|---|---|---|
| Backup ledger | `BackupRun` entity | [`Core/Domain/BackupRun.cs`](../src/Cnas.Ps.Core/Domain/BackupRun.cs) |
| Integrity rows | `BackupIntegrityCheck` | [`Core/Domain/BackupIntegrityCheck.cs`](../src/Cnas.Ps.Core/Domain/BackupIntegrityCheck.cs) |
| Orchestrator | `IBackupOrchestrator` | [`Application/Backups/IBackupOrchestrator.cs`](../src/Cnas.Ps.Application/Backups/IBackupOrchestrator.cs) |
| Target adapter | `IBackupTarget` | [`Application/Backups/IBackupTarget.cs`](../src/Cnas.Ps.Application/Backups/IBackupTarget.cs) |
| Admin endpoints | `BackupAdminController` | [`Api/Controllers/BackupAdminController.cs`](../src/Cnas.Ps.Api/Controllers/BackupAdminController.cs) — `/api/admin/backups/runs`, `runs/{sqid}/retry-integrity-check`, `sweep-expired` |
| Audit chain verifier | `IAuditChainVerifier` | [`Application/Audit/IAuditChainVerifier.cs`](../src/Cnas.Ps.Application/Audit/IAuditChainVerifier.cs) |
| Nightly integrity check | `IntegrityCheckJob` | [`Infrastructure/Jobs/IntegrityCheckJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/IntegrityCheckJob.cs) |

## 4. Procedure

### 4.1 Detect

1. Operator opens `GET /api/admin/backups/runs?status=Failed` (or filters
   by latest run per policy). The endpoint is served by
   `BackupAdminController.ListRunsAsync`.
2. Cross-check with `IntegrityCheckRuns` rows surfaced under
   `/api/admin/integrity-check` (R2282) — a recent run with non-zero
   `IntegrityCheckFinding` rows is corroborating evidence.
3. Confirm severity. If only a single `BackupIntegrityCheck` is
   `HashMismatch`, retry first (§4.2). If the latest run is `Failed` AND
   the primary DB is unreachable, escalate to full restore (§4.3).

### 4.2 Re-verify integrity

1. Invoke `POST /api/admin/backups/runs/{sqid}/retry-integrity-check`.
   The orchestrator re-downloads the payload from the configured
   `IBackupTarget`, re-hashes it, and upserts the
   `BackupIntegrityCheck` row.
2. On `Passed`, the run is authoritative — proceed with restore.
3. On repeated `HashMismatch`, the target object is corrupt — move to
   the prior successful run and emit `BACKUP.INTEGRITY_FAILED` audit.

### 4.3 Restore (primary loss)

> External coordination required: DevOps provisions the replacement
> Postgres instance; CNAS personnel must be onsite to confirm switch-over.
> The MEGA certificate (MPass / MConnect mTLS) may need re-issuance.

1. **Freeze writes.** Scale the API deployment to zero replicas (Helm).
   No application-level read-only mode flag exists yet — write freezes
   are enforced at the deployment surface. (Application-level
   `ReadOnlyMode` toggle is **PARTIAL** — tracked in TODO.md as the
   BCP follow-up; today operators use the deployment scale.)
2. **Provision target.** DevOps creates a new Postgres primary at the
   same PgBouncer endpoint (`Postgres:Pool` connection string remains
   stable). The platform refuses to start without a reachable Postgres
   (`db.postgres` health check fails 503).
3. **Download payload.** Operator calls (or scripts against)
   `IBackupTarget.DownloadAsync(payloadStorageKey)` — the orchestrator
   exposes this via `RetryIntegrityCheckAsync`, which both downloads
   and re-hashes. For an out-of-band restore, the storage key is
   retrievable from the `BackupRun.PayloadStorageKey` column.
4. **Apply payload.** Out-of-app: physical pg_restore. The in-app layer
   is the ledger + integrity proof; the physical restore is owned by
   DevOps (see Helm chart and `BackupPolicy.TargetKind`).
5. **Bring API back up.** Helm `helm upgrade` returns the deployment to
   normal replicas. The startup migration runner replays any missing
   EF migrations against the restored DB (controlled by
   `Cnas:SkipMigrations=false`).

### 4.4 Audit-chain verification after restore

1. Invoke `IAuditChainVerifier.VerifyAsync` (admin surface). The verifier
   walks `AuditLog` rows from the GENESIS literal, recomputing each
   `RowHash` and comparing `PrevHash` linkage.
2. On `IsValid=false`, the report names the first broken row and a
   stable reason (`PrevHashMismatch` or `RowHashMismatch`). Escalate to
   the security team — a break post-restore means audit rows were lost
   between the last successful drain and the backup snapshot.
3. The `AuditArchiveReplayJob` ([`Infrastructure/Jobs/AuditArchiveReplayJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/AuditArchiveReplayJob.cs))
   continues to extend the chain — no extra action needed.

## 5. Validation

- Every restore emits these audit codes (severity Critical):
  `BACKUP.RUN_SUCCEEDED` / `BACKUP.RUN_FAILED` /
  `BACKUP.INTEGRITY_FAILED` / `BACKUP.RETENTION_SWEPT`.
- The integrity check counter (`cnas.backup.integrity_check_outcome`)
  shows the `Passed` increment per retry.
- `AuditChainVerifier` returns a complete report — file it under the
  ticket for compliance traceability.
- Confirm the next scheduled `BackupExecutionJob` fire produces a clean
  run within the policy's cron interval.

## 6. References

- TOR SEC 060 (backup automation), SEC 062 (integrity on crash),
  SEC 063 / SEC 066 (operational restoration), SEC 047 (audit chain).
- [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md) — RTO / RPO
  declarations and BCP scope.
- [`operations.md`](operations.md) — health endpoints + secrets
  configuration.
- Iteration notes: iter 76 (`IntegrityCheckJob`), iter 90
  (`IBackupOrchestrator` + `BackupRun` + `BackupIntegrityCheck`).
