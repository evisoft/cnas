# Contract-end procedures

> Anchored to TOR ID(s): R2507 (PIR 041-043, Phase 13). Iteration 100.
> Reuses the handover artefacts in
> [`source-code-handover.md`](source-code-handover.md) and applies
> retention and successor-cooperation obligations.

## 1. Purpose / scope

Defines the supplier's obligations at the end of the support contract:
final delivery of source code and documentation, the 1-year retention
period, and the 1-year cooperation window with the successor
supplier. Applies once the contract enters its termination phase, by
expiry or by termination clause.

## 2. Audience / stakeholders

Outgoing supplier (engineering, support, security, legal), CNAS
contracting authority, CNAS legal, CNAS operations lead, and the
successor supplier (once designated).

## 3. Procedure

### 3.1 Handover artefact list (PIR 041 — full source + docs)

| # | Artefact | Notes |
|---|---|---|
| 1 | Final source code snapshot at termination tag | `vX.Y.Z-contract-end` |
| 2 | Git mirror including full commit history and tags | Per [`source-code-handover.md`](source-code-handover.md) §3.3 |
| 3 | All EF migrations applied and unapplied | `src/Cnas.Ps.Infrastructure/Migrations/` |
| 4 | All operational guides | [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md) |
| 5 | All architectural and design docs | [`../ARCHITECTURE.md`](../ARCHITECTURE.md), [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md), [`../pm/srs-structural.md`](../pm/srs-structural.md) |
| 6 | BCP / DRP / Backup Plan | [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) |
| 7 | Recovery procedures + DR drill runbook | [`../recovery-procedures.md`](../recovery-procedures.md), [`../dr/dr-drill-runbook.md`](../dr/dr-drill-runbook.md) |
| 8 | Integration specs | [`../integration/technical-integration-specs.md`](../integration/technical-integration-specs.md), [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md) |
| 9 | Migration plans + reconciliation reports | Per [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md) |
| 10 | All training materials | [`../training/training-plan.md`](../training/training-plan.md) |
| 11 | Final monthly support and error-fix reports | Per [`../operations/monthly-error-fix-report-template.md`](../operations/monthly-error-fix-report-template.md) and `IMonthlySupportReportService` (R2461) |
| 12 | Final SLA dossier and pen-test reports | Per Phase 13 |
| 13 | Signed acceptance protocols (all milestones) | Bilateral artefacts |
| 14 | List of every secret and credential — rotated and revoked log | Never the values |
| 15 | Outstanding issues register + roadmap | Hand over to successor |

### 3.2 Retention obligations (PIR 042 — 1-year retention)

1. Supplier retains a sealed, read-only copy of the handover bundle
   for one calendar year after termination.
2. Retention storage is encrypted at rest with a key escrowed with
   CNAS legal.
3. Within retention, restoration assistance must be available within
   five business days of a CNAS written request.
4. At the end of the retention year, the supplier destroys all
   copies and supplies a signed destruction certificate.

### 3.3 IP transfer (PIR 041, anchored to R2103 / R2104)

1. Confirm in writing that all custom code authored under the contract
   is the property of CNAS (R2103 — full IP transfer to CNAS).
2. Confirm data ownership by CNAS and that NDA + DPA terms continue
   to bind the supplier (R2104).
3. List third-party / OSS dependencies with their licences and any
   commercial obligations transferring with the system.

### 3.4 Successor cooperation (PIR 043 — 1-year)

For one year after termination, the supplier:

1. Responds to the successor's clarification requests within the SLA
   bands carried over from PIR 020-023.
2. Joins handover walkthroughs and knowledge-transfer sessions as
   scheduled by CNAS (capped per the contract).
3. Provides root-cause assistance for incidents traceable to code
   delivered under the contract.
4. Reviews and signs off — without authoring — successor-led changes
   that touch the original architecture's load-bearing surfaces.

Cooperation modes:

- Asynchronous: ticketed Q&A via CNAS service desk.
- Synchronous: scheduled video walkthroughs.
- On-site: capped allotment of person-days for critical incidents,
  per the contract schedule.

## 4. Acceptance criteria / sign-off

- All artefacts in §3.1 delivered and acknowledged by CNAS.
- Sealed retention bundle stored and registered with CNAS legal.
- IP transfer letter signed.
- Successor-cooperation schedule signed.
- Final destruction certificate signed at the end of the retention
  year.

## 5. Implementation map

This document is procedural; implementations are the artefacts and
processes it references.

## 6. Status / open gaps

- Termination tag scheme (`vX.Y.Z-contract-end`) — agreed, not yet
  applied.
- Destruction certificate template — pending.
- IP / NDA / DPA contractual artefacts — outside this repository.

## 7. References

- TOR §PIR 041-043
- TODO.md rows R2103, R2104, R2507
- [`source-code-handover.md`](source-code-handover.md)
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
