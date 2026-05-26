# Migration execution runbook

> Anchored to TOR ID(s): R2432 (Task 4.3, Milestone M4). MIG 009
> data-residency: source + staging + target tables never leave the
> CNAS-controlled MCloud tenancy. Companion to R2430 / R2431 / R2433 /
> R2434. Iteration 104.

## 1. Purpose / scope

Step-by-step runbook for the production-Apply execution of an active
`MigrationPlan`. Picks up where `docs/migration/migration-acceptance-protocol.md`
ends (signed DryRun protocol) and drives the cutover through
`IMigrationImporter.ApplyAsync`, validation via `IMigrationReconciler`,
and the sign-off back into the Acceptance Protocol.

In scope: one full Apply pass per registered plan. Out of scope: ongoing
incremental migrations after go-live (tracked under a future iteration
once incremental sources land).

## 2. Audience / stakeholders

- Supplier migration lead (runbook owner, on-call commander).
- Supplier DevOps (maintenance-window co-ordination).
- CNAS data owner (sign-off authority, MIG-009 observer).
- CNAS Service Owner (executive escalation, abort authority).
- Supplier QA + CNAS QA (post-execution validation).
- Acceptance committee (Apply sign-off).

## 3. Pre-flight checks (T-24 h)

Run these against the **target prod tenancy** the day before execution.
**Any RED check halts the run.**

1. **Signed DryRun protocol** present for the exact `MigrationPlan`
   version and commit hash. Schema lock unbroken since signing.
2. **Plan status** = `Active` (via `IMigrationPlanService.GetAsync`).
3. **Reconciliation report** for the latest DryRun = `Passed` OR every
   `Discrepancy` line carries a signed residual acceptance.
4. **Backup smoke:** trigger `BackupPolicy=DB_DAILY_FULL` once on the
   target → verify a new `BackupRun.Status=Succeeded` row + matching
   `BackupIntegrityCheck.Status=Passed`. This is the rollback anchor.
5. **Audit chain** verified end-to-end:
   `IAuditChainVerifier.VerifyAsync` returns `IsValid=true`.
6. **Maintenance window** filed via `IMaintenanceWindowService`
   (Major notice, 10 business days minimum, per R2502).
7. **Peak-hour gate** verified inactive across the planned window
   (Europe/Chisinau 22:00–06:00 by default, or signed override).
8. **On-call rotation** acknowledged in the helpdesk; pager test sent.
9. **Health probes** green on every API + Web pod
   (`/health`, `/health/live`, `/health/ready`).
10. **MIG-009 attestation** on file: the source extract, staging tables,
    and target tables are all inside the CNAS MCloud tenancy. No
    external endpoints involved in this run.

## 4. Schedule

| T | Action | Owner |
|---|---|---|
| **T-10 BD** | File the Major maintenance window (R2502). | Supplier DevOps |
| **T-5 BD** | Notify CNAS users via `IMaintenanceWindowService`. | CNAS Service Owner |
| **T-1 d** | Pre-flight §3. Brief on-call. | Migration lead |
| **T-2 h** | Read-only mode flip (if in scope). Final backup. | Supplier DevOps |
| **T** | **Execute Apply.** §5. | Migration lead |
| **T+30 m** | Reconciliation re-run. §6. | Supplier QA |
| **T+1 h** | Sanity smokes + health checks. §6. | Supplier QA + CNAS QA |
| **T+2 h** | Sign-off + window close. §8. | CNAS Service Owner |

## 5. Execution steps

1. **Begin audit context** — open a Critical audit row
   `MIGRATION.APPLY_STARTED` with `MigrationPlan.Code` + commit hash.
2. **Snapshot target** — confirm the pre-flight `BackupRun.Sha256` and
   record it in the runbook ticket (rollback anchor).
3. **Invoke importer** — call `IMigrationImporter.ImportAsync(planId,
   mode: Apply, …)` via the admin REST surface
   (`MigrationAdminController`). The job writes `MigrationStagingRow`
   batches with deterministic mapper traces.
4. **Watch counters** — supervise the live OTel counters
   (`cnas.migration.rows_imported`, `cnas.migration.rows_rejected`,
   `cnas.migration.batch_completed`).
5. **First reconciliation** — once the importer reports `Completed`,
   trigger `IMigrationReconciler.ReconcileAsync` and persist the
   `ReconciliationReport`.
6. **Compare to DryRun baseline** — diff must equal the accepted DryRun
   outcome (Passed or signed residual). Any deviation → §7 abort.
7. **Audit chain re-verify** — `IAuditChainVerifier.VerifyAsync` =
   `IsValid=true`.
8. **Emit completion audit** — `MIGRATION.APPLY_COMPLETED` Critical row
   with `ReconciliationReport.Id`.

## 6. Post-execution validation

- `MigrationPlan` lifecycle: `Active → Archived` once production traffic
  resumes (via `IMigrationPlanService.ArchiveAsync`).
- `ReconciliationReport.Status = Passed` (or signed residual).
- Sample queries: 100 random source rows located in the target with
  field-by-field equality (QA spot-check script).
- `/api/health/database` returns `200` with primary + replica healthy.
- `IntegrityCheckJob` next run completes with zero new Critical findings.
- `AuditChainIntegrityCheck` next run = Passed.

## 7. Abort criteria

The run aborts immediately on any of:

- Importer exception that the retry policy cannot recover within 3
  attempts.
- Reconciliation diff that deviates from the DryRun baseline.
- Audit-chain verification failure (`IsValid=false`).
- Backup integrity-check downgrade between pre-flight and post-execution.
- Health-probe RED on any API/Web pod for > 5 min during the window.
- CNAS Service Owner instructs abort.

**Abort procedure:**

1. Write Critical audit `MIGRATION.APPLY_ABORTED` with reason.
2. Restore target from the pre-flight `BackupRun.Sha256` via the
   recovery procedure (`docs/recovery-procedures.md`).
3. Confirm audit-chain integrity re-verified after restore.
4. Open a Critical SupportTicket; convene retro before any retry.

## 8. Sign-off

The Apply run signs off when:

- Steps §3, §5, §6 all complete green.
- `ReconciliationReport.Status = Passed` (or signed residual).
- Bilateral protocol page from `docs/migration/migration-acceptance-protocol.md`
  is countersigned with Apply outcomes attached.
- Maintenance window closed; user-visible traffic resumed.
- Row "Task 4.3 / R2432" updated in the Acceptance Protocol.

## 9. References

- TOR §Task 4.3 / §MIG 009.
- `src/Cnas.Ps.Application/Migration/IMigrationPlanService.cs`,
  `IMigrationImporter.cs`, `IMigrationReconciler.cs`.
- `src/Cnas.Ps.Api/Controllers/MigrationPlansController.cs`,
  `MigrationAdminController.cs`.
- `docs/migration/migration-acceptance-protocol.md` (R2434).
- `docs/recovery-procedures.md` (rollback path).
- `docs/bcp-drp-backup-plan.md`, `docs/dr/dr-drill-runbook.md`.
- `docs/operations/mcloud-environments.md` (residency posture).
