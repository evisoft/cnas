# Admin training specification (≥2 admins, ≥64 hours)

> Anchored to TOR ID(s): R2441 (UTD 007, Milestone M5). Companion to
> R2440 ([`training-plan.md`](training-plan.md)). Iteration 103.

## 1. Purpose

Define the minimum admin training programme so CNAS owns the platform's
day-to-day administration before final acceptance. Locks in the
TOR-mandated quantitative thresholds: at least two System
Administrators, each completing at least 64 instruction hours, in
Romanian and English.

## 2. Audience / stakeholders

- CNAS System Administrators (≥ 2 named persons, designated by CNAS HR).
- Supplier training lead, supplier security officer (auditor).
- Joint acceptance committee for UTD 007.

## 3. Curriculum and hours per module

| # | Module | Hours | Anchors |
|---|---|---|---|
| 1 | Platform topology, environments, configuration | 6 | `docs/ARCHITECTURE.md`, `docs/pm/tech-infra-requirements.md` |
| 2 | ABAC scopes, role management, audit explorer | 8 | `AbacAdminController`, iter-88 ABAC, iter-95 audit chain |
| 3 | Workflow definitions & change-request 4-eyes++ | 8 | `IChangeRequestService` (iter 81/94) |
| 4 | Backup, restore, BCP / DRP drills | 8 | `docs/bcp-drp-backup-plan.md`, `docs/recovery-procedures.md`, `docs/dr/dr-drill-runbook.md` |
| 5 | Performance monitoring & capacity | 6 | `docs/performance-ops.md`, `docs/performance-kpis.md` |
| 6 | Integrations operating procedures | 6 | `docs/integration/technical-integration-specs.md`, `docs/integration/interop-acceptance-protocol.md` |
| 7 | Migration administration (registry + dry-run + apply) | 6 | `docs/migration/migration-acceptance-protocol.md` |
| 8 | Helpdesk, SLA configuration, escalation | 6 | iter-92 `SupportTicketCategory` + `SupportTicketSlaEvaluator` |
| 9 | Reporting (monthly support, error-fix, unplanned-dev burn-down) | 4 | `docs/operations/monthly-support-report-template.md`, `docs/operations/monthly-error-fix-report-template.md` |
| 10 | Production deployment + rollback drill | 4 | `docs/production-deployment.md`, `docs/go-live-strategy.md` |
| 11 | Final assessment (practical + written) | 2 | This spec §4 |
| | **Total** | **64** | UTD 007 minimum |

## 4. Acceptance criteria

- ≥ 2 named CNAS admins enrolled and attended ≥ 64 hours each.
- Attendance log signed per session by learner + trainer.
- Final assessment ≥ 75% per learner (practical lab + written).
- Lab tasks performed in the staging tenant (not production).
- Bilingual delivery (RO + EN) certified by training lead.
- Sign-off entered in the Acceptance Protocol row "UTD 007 / R2441"
  (`docs/acceptance/acceptance-protocol-template.md`).

## 5. Status / open gaps

- Named learners and exact dates: pending CNAS HR confirmation.
- Slide decks and lab guides: pending (parent R2440).
- Final assessment item bank: pending.
- No automated trainer recert cadence yet (carry over from R2440).

## 6. References

- TOR §UTD 007
- TODO.md R2441 (this row), R2440 (parent plan)
- [`training-plan.md`](training-plan.md)
- [`trainer-training-spec.md`](trainer-training-spec.md)
- [`end-user-training-spec.md`](end-user-training-spec.md)
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
