# Operational guides — index

> Anchored to TOR ID(s): R2444 (UTD 013, Milestone M5). Master index of
> operator-facing documentation. Iteration 100.

## 1. Purpose / scope

Single entry point for everything an operator (SRE, DBA, support tier
1-3, CNAS ops staff) needs to keep SI „Protecția Socială" running.
Cross-links to per-area runbooks. Sole entry surface for UTD 013.

## 2. Audience / stakeholders

SREs, DBAs, support tiers 1-3, CNAS ops shift leads, and the
acceptance committee for UTD 013.

## 3. Index

### 3.1 Performance & capacity

- [`../performance.md`](../performance.md) — performance architecture.
- [`../performance-ops.md`](../performance-ops.md) — operator-facing
  performance runbook (PSR 005 / PSR 007).
- [`../performance-kpis.md`](../performance-kpis.md) — KPI targets.

### 3.2 Continuity, backup & recovery

- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) — BCP / DRP
  / Backup Plan (R2459, Deliverable 6.2).
- [`../recovery-procedures.md`](../recovery-procedures.md) — operator
  recovery runbook (SEC 063 / SEC 066).
- [`../dr/dr-drill-runbook.md`](../dr/dr-drill-runbook.md) — quarterly
  DR drill runbook (R2708).

### 3.3 Deployment & change

- [`../go-live-strategy.md`](../go-live-strategy.md) — go-live phasing
  proposal (R2454, COM 001).
- [`../production-deployment.md`](../production-deployment.md) —
  deployment + rollback plan (R2455, COM 002).

### 3.4 Integrations

- [`../integration/technical-integration-specs.md`](../integration/technical-integration-specs.md) —
  per-touchpoint contracts and adapters (R2423).
- [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md) — current
  protocol-level gap audit.

### 3.5 Data migration

- [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md) —
  bilateral acceptance flow (R2434).

### 3.6 Reporting & metrics

- [`monthly-error-fix-report-template.md`](monthly-error-fix-report-template.md) —
  monthly error-fix + doc-update report template (R2462).
- Monthly support report — produced by
  `IMonthlySupportReportService` (R2461).

### 3.7 Handover & lifecycle

- [`../handover/source-code-handover.md`](../handover/source-code-handover.md) —
  source / repo access handover (R2445, UTD 014).
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) —
  contract-end retention + cooperation (R2507).

### 3.8 Training

- [`../training/training-plan.md`](../training/training-plan.md) —
  training plan (R2440, UTD 002 / UTD 011-012).

### 3.9 Day-to-day operations (existing)

- [`../operations.md`](../operations.md) — operations overview.
- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — architecture map.
- [`../design-system.md`](../design-system.md) — UI design system.

### 3.10 Admin-feature guides — future stubs

Stubs to be drafted per admin feature once UATs sign off:

- ABAC scope administration.
- Backup policy administration.
- Change-request lifecycle administration.
- Workflow definition authoring.
- Audit explorer.
- Help-desk SLA configuration.

## 4. Acceptance criteria / sign-off

- Every link above resolves to an existing file (or is explicitly
  marked as a future stub).
- Operator on-call can navigate from this index to any runbook within
  one click.
- CNAS ops lead signs the index as part of UTD 013 acceptance.

## 5. Implementation map

This document is a navigation surface. Implementations live in the
referenced runbooks.

## 6. Status / open gaps

- Admin-feature guide stubs in §3.10 are not yet authored.
- Some referenced runbooks carry PLACEHOLDER values (RTO/RPO) pending
  CNAS confirmation.

## 7. References

- TOR §UTD 013
- TODO.md R2444 / R2459 / R2461 / R2462 / R2454 / R2455 / R2708
