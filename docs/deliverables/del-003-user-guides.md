# DEL 003 — User guides (index)

> Anchored to TOR ID(s): R2602 (TOR §7.1). Index doc; content lives
> in the linked files. Iteration 103.

## 1. Purpose

Single navigation surface for the user-facing deliverables required
by TOR §7.1 DEL 003: admin guide, install/deploy/config guide,
current-maintenance guide, defect-removal guide, per-app user guide,
developer guide, video instructions, ISMS docs (BCP / DRP / Backup).

## 2. Audience

CNAS end users, administrators, operators, developers (CNAS internal +
successor supplier), acceptance committee.

## 3. Bundle contents

| Artefact | File | Status |
|---|---|---|
| Operational guides — master index | [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md) | Iter 100 |
| Training plan (audience tiers, syllabi) | [`../training/training-plan.md`](../training/training-plan.md) | Iter 100 |
| BCP / DRP / Backup Plan (ISMS) | [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) | Iter 98 |
| Recovery procedures (operator) | [`../recovery-procedures.md`](../recovery-procedures.md) | Iter 98 |
| Performance ops runbook | [`../performance-ops.md`](../performance-ops.md) | Earlier iter |
| Production deployment guide | [`../production-deployment.md`](../production-deployment.md) | Iter 98 |
| Go-live strategy | [`../go-live-strategy.md`](../go-live-strategy.md) | Iter 98 |
| Integrations operating guide | [`../integration/technical-integration-specs.md`](../integration/technical-integration-specs.md) | Iter 100 |
| Architecture overview (developer guide) | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) | Repo root |
| Design system (UI developer guide) | [`../design-system.md`](../design-system.md) | Earlier iter |

## 4. Pending per-app user guide stubs

Per-app end-user guides are pending and tracked as stubs in
[`../operations/operational-guides-index.md`](../operations/operational-guides-index.md) §3.10:

- ABAC scope administration.
- Backup policy administration.
- Change-request lifecycle administration.
- Workflow definition authoring.
- Audit explorer.
- Help-desk SLA configuration.
- Citizen portal (per benefit family) — `applications/new`, `inbox`,
  `dashboard`.

Video walkthroughs (UTD 011-012) are pending and tracked under R2440
(training plan §4).

## 5. Acceptance criteria

- Operational-guides index resolves every linked artefact (R2444).
- BCP / DRP / Backup Plan signed (R2459).
- Per-app user guide stubs filled before UTD 013 final sign-off.
- Video walkthroughs delivered in RO + RU before UTD 011-012 sign-off.
- CNAS Service Owner signs DEL 003 row in the Acceptance Protocol.

## 6. Status / open gaps

- Per-app user guides: stubs only — drafting blocked until UATs sign
  off each feature.
- Video walkthroughs: not yet recorded (R2440 child task).
- Developer guide: currently spread across `ARCHITECTURE.md`,
  `design-system.md`, and SDD; consolidation pending.

## 7. References

- TOR §7.1 DEL 003
- TODO.md R2602 (this row), R2440, R2444, R2459
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
