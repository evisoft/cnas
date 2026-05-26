# Disaster-recovery drill runbook

> Anchored to TOR ID(s): R2708 (Verification §19). Companion to
> [`../recovery-procedures.md`](../recovery-procedures.md) and
> [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md). Iteration 100.

## 1. Purpose / scope

Step-by-step procedure for the recurring DR drill: simulate database
loss on a non-production replica, restore from the most recent
backup, validate audit-chain integrity and application health, and
record the achieved RTO against the documented target. Run every
quarter; mandatory before each production go-live phase.

## 2. Audience / stakeholders

Drill coordinator (supplier SRE lead), CNAS DBA, CNAS SRE, security
officer (observes audit-chain replay), and the joint acceptance
committee for R2708.

## 3. Pre-requisites

| Item | Value |
|---|---|
| Target environment | Isolated staging — never production |
| Most recent backup | `BackupRun.Status = Completed` with `Sha256` populated |
| RTO target | **PLACEHOLDER ≤ 4 hours** (confirm in `bcp-drp-backup-plan.md`) |
| RPO target | **PLACEHOLDER ≤ 1 hour** |
| Notice | Maintenance window — Major class (10 business days) if any user-facing surface is touched |
| Audit | Drill steps recorded as Critical-severity audit rows |

## 4. Drill procedure

### 4.1 Phase A — Setup

1. Drill coordinator opens a `ChangeRequest` (Draft) describing the
   drill and tags it `DR_DRILL`.
2. CNAS reviewers approve and transition to `Scheduled`.
3. Coordinator picks the target replica and confirms no live traffic
   is bound to it.
4. Take a final snapshot of the target replica's current state for
   rollback safety.

### 4.2 Phase B — Simulate DB loss

1. Stop the application instance bound to the target replica.
2. Drop the database (or detach the volume) on the target replica.
3. Start a stopwatch — this is `t0` for RTO measurement.

### 4.3 Phase C — Restore

1. Identify the latest `BackupRun` (Completed) for `DB_DAILY_FULL`
   (and the most recent `DB_HOURLY_INCR` if applicable).
2. Verify `Sha256` against the stored ledger value — abort if mismatch.
3. Restore using the documented restore command for the chosen
   backup target (see `BackupAdminController` + recovery procedures).
4. Apply incrementals up to the RPO target.
5. Run EF migrations to confirm the schema is current.

### 4.4 Phase D — Validate

1. Start the application instance against the restored DB.
2. Run `IAuditChainVerifier.VerifyAsync` — must return a clean chain.
3. Hit `GET /health` — must return 200.
4. Run a synthetic read against each critical aggregate (decisions,
   applications, payments).
5. Run a synthetic write end-to-end (create application →
   decision → payment intent), bracketed by audit assertions.
6. Stop the stopwatch — record `t1`. RTO actual = `t1 - t0`.

### 4.5 Phase E — Sign-off and teardown

1. Drill coordinator files the drill report (timings, anomalies,
   audit-chain verification result).
2. CNAS DBA and SRE sign the report.
3. Coordinator transitions the `ChangeRequest` to `Closed` (success)
   or `RolledBack` (failure) per the `IChangeRequestService` flow.
4. Coordinator restores the replica to its pre-drill snapshot if it
   will rejoin a higher-environment role.

## 5. Validation checklist

- [ ] `BackupRun.Sha256` matches ledger value.
- [ ] DB restore completes within RTO target.
- [ ] EF migrations apply cleanly.
- [ ] Audit chain verifies — `IAuditChainVerifier` returns no breaks.
- [ ] `/health` returns 200 for every checked dependency.
- [ ] Synthetic read of each critical aggregate succeeds.
- [ ] Synthetic write + audit row visible.
- [ ] Drill report filed with timings and signatures.
- [ ] `ChangeRequest` closed in correct terminal state.

## 6. Acceptance criteria / sign-off (R2708)

- The drill is executed at least once during M6 stabilization and at
  least quarterly thereafter.
- The achieved RTO is documented and is ≤ the documented target.
- The audit-chain verifier returns clean for the restored database.
- The drill report is signed by both supplier and CNAS.

## 7. Implementation map

| Surface | Path |
|---|---|
| Backup ledger | `src/Cnas.Ps.Core/Domain/BackupRun.cs` |
| Backup admin endpoints | `src/Cnas.Ps.Api/Controllers/BackupAdminController.cs` |
| Audit chain verifier | `src/Cnas.Ps.Application/Audit/IAuditChainVerifier.cs` |
| Health endpoint | `src/Cnas.Ps.Api/Controllers/HealthDatabaseController.cs` + ASP.NET health checks |
| Change-request workflow | `IChangeRequestService` (Draft → Scheduled → Deployed → RolledBack) |
| Recovery procedures | [`../recovery-procedures.md`](../recovery-procedures.md) |
| BCP / DRP plan | [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) |

## 8. Status / open gaps

- RTO and RPO targets carry PLACEHOLDER values until CNAS confirms
  during stabilization (same as recovery-procedures §2).
- The exact restore command depends on the chosen `IBackupTarget`
  implementation in production (`S3CompatibleBackupTarget` is partial).
- READ_ONLY_MODE toggle while restoring is referenced but not yet
  wired in operations.

## 9. References

- TOR §19 R2708
- TODO.md row R2708
- [`../recovery-procedures.md`](../recovery-procedures.md)
- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
