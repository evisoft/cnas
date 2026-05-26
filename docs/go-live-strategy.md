# Go-live strategy proposal

> Anchored to TOR COM 001 (supplier must justify the chosen production-launch
> strategy). Implementation references are file paths. Companion to
> [`production-deployment.md`](production-deployment.md) and
> [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md).

## 1. Scope

This document compares the four standard go-live strategies and proposes
the optimal sequence for SI „Protecția Socială". It serves CNAS leadership
during the M6 acceptance window and the supplier's deployment manager.

## 2. Objectives

- Limit blast radius of an unforeseen production defect.
- Preserve continuity of pension and social-aid disbursements during the
  cut-over.
- Reuse the migration registry already in code rather than re-inventing
  a parallel cut-over machinery.
- Anchor the timeline to deliverable D6.2 and the M6 stabilization
  3-month clock (STAB 001).

## 3. Implementation map

| Capability | Where |
|---|---|
| Migration plan registry + lifecycle | [`Application/Migration/IMigrationPlanService.cs`](../src/Cnas.Ps.Application/Migration/IMigrationPlanService.cs) (`Draft → Approved → Active ↔ Suspended → Archived`) |
| Migration importer | [`Infrastructure/Services/Migration/MigrationImporter.cs`](../src/Cnas.Ps.Infrastructure/Services/Migration/MigrationImporter.cs) |
| Migration staging | `MigrationStagingRow` — [`Core/Domain/MigrationStagingRow.cs`](../src/Cnas.Ps.Core/Domain/MigrationStagingRow.cs) |
| Migration DryRun job | [`Infrastructure/Jobs/MigrationDryRunJob.cs`](../src/Cnas.Ps.Infrastructure/Jobs/MigrationDryRunJob.cs) |
| Reconciler | [`Infrastructure/Services/Migration/MigrationReconciler.cs`](../src/Cnas.Ps.Infrastructure/Services/Migration/MigrationReconciler.cs) |
| Maintenance window state machine | [`Application/ServiceManagement/IMaintenanceWindowService.cs`](../src/Cnas.Ps.Application/ServiceManagement/IMaintenanceWindowService.cs) |
| Change request workflow | [`Application/ServiceManagement/IChangeRequestService.cs`](../src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs) |

## 4. Procedure — strategy comparison

| Strategy | Pros | Cons | When appropriate |
|---|---|---|---|
| **Big-bang** | One short cut-over; cleanest data state. | Total exposure if a defect surfaces; no fallback to legacy. | Small systems where rollback is cheap. **Not appropriate for CNAS** — 1 500 + 500 users + 300 k tx/day. |
| **Parallel running** | Legacy + new run side-by-side; allows direct compare. | Doubles operational cost; reconciliation overhead; staff confusion. | When legacy must remain authoritative during user training. Possible bridge during pilot. |
| **Phased rollout** | Limits blast radius; learnings inform next phase. | Longer overall timeline; partial-data complications. | Geographic / functional rollouts. Strong fit for CNAS regional structure. |
| **Pilot** | One controlled site validates the platform; cheap to abort. | Pilot site must accept early-adopter risk. | First production exposure of any non-trivial system. |

### 4.1 Recommended sequence — PILOT, then PHASED

1. **Pilot (Weeks 1-4 of M6 stabilization)**
   - Select 1-2 local CTAS offices (small caseload, willing staff,
     reachable for onsite supplier support per STAB 002).
   - Activate the office-scoped `MigrationPlan` rows via
     `IMigrationPlanService.ActivateAsync`. The plan registry enforces
     four-eyes (`SubmitForApprovalAsync` → second admin's
     `ApproveAsync` → `ActivateAsync`).
   - Run `IMigrationImporter` against the pilot office's legacy
     extract. `MigrationDryRunJob` runs nightly in `OffPeakOnly`
     against the staging slice to surface mapping defects before each
     batch.
   - `MigrationReconciler` produces the daily reconciliation report
     (`ReconciliationReport`) signed off by CNAS pilot lead.
2. **Phased rollout (Weeks 5-12 of M6)**
   - Group offices by region (Chișinău, Bălți, Cahul, Comrat, etc.).
   - Each region's go-live is one `ChangeRequest` (see
     [`production-deployment.md`](production-deployment.md)) +
     one `MaintenanceWindow` (Major kind ≥ 10 business-days notice,
     see `IMaintenanceWindowService.MajorMinNoticeBusinessDays`).
   - Activate that region's `MigrationPlan` rows via the same
     four-eyes flow.
   - Decommission legacy disbursement only after one full calendar
     month of clean reconciliation for the region.
3. **National cut-over (end of Week 12, into STAB)**
   - Last region activates; legacy decommissioned globally.
   - Stabilization clock continues per STAB 001 (3 months).

### 4.2 Parallel-running bridge

For the highest-risk flow (pension payment cycles), legacy continues
to run read-only for one calendar month after each region's activation
to allow CNAS auditors to spot-check sums. The bridge is purely
read-only on the legacy side — all writes flow through CNAS „Protecția
Socială" once a region is Active.

## 5. Validation

- Every `MigrationPlan` transition writes a Critical-severity audit
  row (`MIGRATION.PLAN_TRANSITIONED`) — used as evidence in the
  acceptance protocol (COM 004).
- Every regional go-live is gated by:
  - A `ChangeRequest` in state `Deployed` (four-eyes++).
  - A `MaintenanceWindow` in state `Completed`.
  - A `ReconciliationReport` accepted by CNAS for the region.
- The migration DryRun counter (`cnas.migration.dryrun.*`) shows
  zero criticals before activation per office.

## 6. References

- TOR COM 001 (strategy justification), COM 003 (plan coordinated
  with CNAS), COM 004 (acceptance protocol), STAB 001 / STAB 002.
- [`production-deployment.md`](production-deployment.md) — the per-region
  deployment + rollback mechanics.
- [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md) — fallback if a
  region rollout fails.
- Iteration notes: iter 89 (migration plan registry + importer +
  reconciler + DryRun job), iter 93 (maintenance windows), iter 94
  (change request workflow).
