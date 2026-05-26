# Production deployment + rollback plan

> Anchored to TOR COM 002 (supplier-led deployment plan + rollback plan).
> Implementation references are file paths. Companion to
> [`go-live-strategy.md`](go-live-strategy.md) (when each deployment fires)
> and [`recovery-procedures.md`](recovery-procedures.md) (post-rollback
> data restore).

## 1. Scope

This document specifies how each production deployment of SI „Protecția
Socială" is approved, executed, and — if needed — reversed. It serves the
supplier's release manager, CNAS DevOps, and the change advisory board.
It applies to every production release after the initial go-live.

## 2. Objectives

- Enforce four-eyes++ separation between requester, tester, signer, and
  approver for every production change (PIR 030-033).
- Honour the maintenance-window notice cadence (PIR 025).
- Provide a deterministic rollback path on detection of any L1 (critical)
  defect within the deployment window.
- Emit a stable audit trail under Critical severity for every state
  transition.

## 3. Implementation map

| Capability | Where |
|---|---|
| Change-request workflow | [`Application/ServiceManagement/IChangeRequestService.cs`](../src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs) — `Draft → Submitted → InReview → TestEnvValidated → CodeSigned → ApprovedForProd → Deploying → Deployed → (RolledBack)` |
| Change-request controller | [`Api/Controllers/ChangeRequestsController.cs`](../src/Cnas.Ps.Api/Controllers/ChangeRequestsController.cs) — `/api/admin/change-requests/*` |
| Maintenance window service | [`Application/ServiceManagement/IMaintenanceWindowService.cs`](../src/Cnas.Ps.Application/ServiceManagement/IMaintenanceWindowService.cs) |
| Maintenance window controller | [`Api/Controllers/MaintenanceWindowsController.cs`](../src/Cnas.Ps.Api/Controllers/MaintenanceWindowsController.cs) — `/api/admin/maintenance-windows/*` |
| System-update schedule | [`Application/ServiceManagement/ISystemUpdateScheduleService.cs`](../src/Cnas.Ps.Application/ServiceManagement/ISystemUpdateScheduleService.cs) |
| System-update notification job | [`Infrastructure/Jobs/SystemUpdateNotificationJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/SystemUpdateNotificationJob.cs) |
| Helm chart | [`ops/k8s/cnas-ps/`](../ops/k8s/cnas-ps/README.md) |
| Backup ledger (rollback fallback) | [`Application/Backups/IBackupOrchestrator.cs`](../src/Cnas.Ps.Application/Backups/IBackupOrchestrator.cs) |

## 4. Procedure

### 4.1 Notice cadence (PIR 025)

`IMaintenanceWindowService` enforces per-kind minimum advance notice:

| Kind | Min notice | Max duration | Constant |
|---|---|---|---|
| Ordinary | 5 business days | 4 hours | `OrdinaryMinNoticeBusinessDays` / `OrdinaryMaxHours` |
| Major | 10 business days | 24 hours | `MajorMinNoticeBusinessDays` / `MajorMaxHours` |
| Urgent | Immediate | 2 hours | (no min notice) / `UrgentMaxHours` |

For every region cut-over in [`go-live-strategy.md`](go-live-strategy.md)
the supplier creates one `Major` window. Routine releases use `Ordinary`.
Defect hot-fixes use `Urgent` and require explicit CNAS sign-off in the
window's `Reason` field.

### 4.2 Deployment — four-eyes++ flow

1. **Requester** creates the change in Draft via
   `IChangeRequestService.CreateAsync` (controller endpoint
   `POST /api/admin/change-requests`). Submits via `SubmitAsync`.
2. **Reviewer** moves to InReview via `StartReviewAsync`.
3. **Test-env validator** (must be a different user from the requester)
   records validation via `ValidateTestEnvAsync` with the
   test-environment evidence link in `ChangeRequestTestValidationInputDto`.
4. **Code signer** (must differ from requester AND tester) records the
   detached digital signature of the built artefact via
   `SignCodeAsync`.
5. **Approver** (must differ from requester / tester / signer) approves
   for prod via `ApproveAsync`. State becomes `ApprovedForProd`.
6. **Release manager** transitions `StartDeploymentAsync` →
   executes `helm upgrade cnas-ps --version=<release>` against the
   production cluster → on success, calls `CompleteDeploymentAsync`.
7. `SystemUpdateNotificationJob` (always-on profile) fires user-facing
   notifications according to the active `SystemUpdateSchedule` rows.

### 4.3 Rollback plan

> External coordination required: CNAS must authorise rollback in
> writing. The supplier may not roll back unilaterally past the
> `Deployed` state.

1. **Trigger.** Any L1 defect detected during the deployment window or
   the immediately following business day.
2. **Application rollback (configuration / code only).**
   - Call `IChangeRequestService.RollBackAsync(changeSqid, reason)` with
     the rollback evidence in `ChangeRequestRollbackInputDto`. State
     transitions `Deploying|Deployed → RolledBack`.
   - Execute `helm rollback cnas-ps <revision>` to the prior revision.
     `Cnas:SkipMigrations=true` is **not** set in production — EF
     migrations re-run on rollback startup. If a migration is forward-
     only, the rollback requires DBA-driven SQL inverse (DevOps owns
     the inverse script per change).
3. **Data rollback (if data corruption).** Follow
   [`recovery-procedures.md`](recovery-procedures.md) §4.3 — restore
   from the most recent passing `BackupRun`.
4. **Audit.** The transition emits `CHG.ROLLED_BACK` (Critical). The
   maintenance window stays `InProgress` until the rollback completes,
   then transitions to `Completed` via `CompleteAsync` with the rollback
   noted in the window's reason field.

### 4.4 Forward-only migration discipline

Every EF migration is forward-only by convention. Each change request
that ships a new migration MUST include an explicit inverse SQL script
in the `ChangeRequest` evidence. This is enforced procedurally — there
is no automated rollback for schema changes. **PARTIAL** automation —
tracked at TODO.md R-pending; today this is an operator obligation
documented per change.

## 5. Validation

- The change-request state machine validates four-eyes++ in
  `ChangeRequestService` (constructor checks user uniqueness against
  the prior actor on every transition). Architecture tests
  pin transitions.
- Every state transition emits a Critical-severity audit row with the
  code listed in `IChangeRequestService` (`CHG.SUBMITTED`,
  `CHG.TEST_ENV_VALIDATED`, etc.). The Compliance team uses these for
  acceptance evidence (COM 004).
- Maintenance-window notice violations fail with a stable error code
  before the window may transition to `Approved` — the validator runs
  inside `IMaintenanceWindowService.PostNoticeAsync`.
- Health endpoints (`/health/ready`) MUST report `Healthy` before
  `CompleteDeploymentAsync` is called; the release manager records the
  probe output in the change-request evidence.

## 6. References

- TOR COM 002 (deployment + rollback plan), COM 003 (plan coordinated
  with CNAS), PIR 025 (notice cadence), PIR 030-033 (change discipline).
- [`go-live-strategy.md`](go-live-strategy.md) — when each deployment
  fires.
- [`recovery-procedures.md`](recovery-procedures.md) — data-loss
  rollback path.
- [`operations.md`](operations.md) — Helm chart pointer, health
  endpoints.
- Iteration notes: iter 93 (`IMaintenanceWindowService` +
  `ISystemUpdateScheduleService`), iter 94 (`IChangeRequestService`
  four-eyes++ workflow).
