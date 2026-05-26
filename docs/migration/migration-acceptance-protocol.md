# Migration acceptance protocol

> Anchored to TOR ID(s): R2434 (Deliverable 4.3, Milestone M4). Companion
> to R2430-R2433 (plan / scripts / execution / reconciliation). Iteration
> 100.

## 1. Purpose / scope

Define the bilateral acceptance procedure that signs off each migration
plan run before the imported data becomes the system of record. Applies
to every active `MigrationPlan` registered via `IMigrationPlanService`.
Covers DryRun acceptance, production-Apply acceptance, and the
reconciliation sign-off.

## 2. Audience / stakeholders

Supplier migration lead, CNAS data owner, supplier QA, CNAS QA, MIG-009
data-residency observer (data never leaves CNAS infrastructure), and
the joint acceptance committee.

## 3. Procedure (numbered)

1. **Plan registration.** Supplier files the `MigrationPlan` via
   `IMigrationPlanService.CreateAsync`; CNAS owner moves it
   `Draft → Approved` via the admin REST surface.
2. **Schema lock.** Source schema and target mapping are committed and
   tagged in the repository. Any subsequent change re-enters the
   Draft state.
3. **DryRun execution.** `IMigrationImporter` is invoked in DryRun mode.
   The `MigrationDryRunJob` (Quartz, 02:15 UTC daily) automates the
   recurring case. Results land in `MigrationStagingRow`.
4. **Reconciliation compute.** `IMigrationReconciler.ReconcileAsync`
   produces a `ReconciliationReport` (Passed / Discrepancy / Failed)
   with PII-free fingerprints.
5. **Joint review.** Acceptance committee reviews the report; any
   Discrepancy must be itemised and either auto-corrected (mapper
   patch + re-run) or formally accepted as residual.
6. **Sign-off — DryRun.** Supplier and CNAS sign the dry-run protocol
   for that plan version.
7. **Apply execution.** `IMigrationImporter` is invoked in Apply mode,
   bracketed by `MaintenanceWindow` (Major notice if user-visible).
8. **Reconciliation re-run.** Step 4 is repeated against the applied
   data; the comparison must equal the accepted DryRun outcome.
9. **Sign-off — Apply.** Final bilateral protocol is signed; plan
   transitions `Active → Archived` once production traffic resumes.

## 4. Acceptance criteria / sign-off

- `ReconciliationReport.Status = Passed` OR every Discrepancy line is
  signed off as residual.
- Zero PII fields appear in fingerprints (`MigrationReconciler` enforces).
- The audit trail emits `MIGRATION.RECONCILIATION_COMPUTED` rows for
  both DryRun and Apply.
- All `MigrationStagingRow` entries have a deterministic mapper trace.
- Both parties sign the protocol; the signed artefact is attached as a
  document version to the `MigrationPlan`.

## 5. Implementation map

| Surface | Path |
|---|---|
| Plan registry & lifecycle | `src/Cnas.Ps.Application/Migration/IMigrationPlanService.cs` |
| Plan service implementation | `src/Cnas.Ps.Infrastructure/Services/Migration/MigrationPlanService.cs` |
| Importer (DryRun + Apply) | `src/Cnas.Ps.Application/Migration/IMigrationImporter.cs` → `…/Migration/MigrationImporter.cs` |
| Reconciler | `src/Cnas.Ps.Application/Migration/IMigrationReconciler.cs` → `…/Migration/MigrationReconciler.cs` |
| Recurring DryRun job | `src/Cnas.Ps.Infrastructure/Jobs/MigrationDryRunJob.cs` (Quartz, 02:15 UTC) |
| Admin REST surface | `src/Cnas.Ps.Api/Controllers/MigrationPlansController.cs`, `MigrationAdminController.cs` |
| Staging table | `MigrationStagingRow` (EF migration `AddMigrationRegistry`) |

## 6. Status / open gaps

- Bilateral acceptance template signed by CNAS — pending.
- R2432 (Migration execution) — execution gated by signed DryRun
  protocol; production cutover plan referenced in
  [`../go-live-strategy.md`](../go-live-strategy.md).
- MIG-009 data-residency attestation form — pending (bilateral artefact).

## 7. References

- TOR §4 Data migration
- [`../go-live-strategy.md`](../go-live-strategy.md)
- [`../production-deployment.md`](../production-deployment.md)
- TODO.md rows R2430-R2434
