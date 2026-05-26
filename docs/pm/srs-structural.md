# Software Requirements Specification (SRS) — Structural

> Anchored to TOR ID(s): R2402 (Task 1.3, Deliverable 1.3). This is the
> structural skeleton. The authoritative running register of functional
> requirements (R0XXX IDs) lives in `TODO.md`; pointers below reference the
> actual implementation files in this repository.

## 1. Purpose

Provide the contractual list of *what the system must do*, mapped against the
existing implementation. Used by Beneficiary, Architect, QA, and Operator.

## 2. Scope

Covers SI „Protecția Socială" — pensions, social benefits, special pensions
(athletes, internal affairs, prosecutors, judges, parliamentarians),
international agreements, contributor accounts, payer declarations, and
helpdesk. References TOR §1–§5 + §16 milestones.

## 3. Content / Sections

### 3.1 Scope and actors (TOR §1)

System actors and their domain entities:

- Beneficiar (Solicitant) — citizen requesting a benefit. Entity:
  `src/Cnas.Ps.Core/Domain/Solicitant.cs`.
- Contribuabil — payer / contributor; entity:
  `src/Cnas.Ps.Core/Domain/Contributor.cs` and the related
  `ContributorActivityPeriod`, `ContributorSocialInsuranceContract`,
  `ContributorPre1999PeriodCarnetMunca` aggregates.
- Internal users — back-office staff. Entity:
  `src/Cnas.Ps.Core/Domain/UserProfile.cs` (iter 74). Groups:
  `src/Cnas.Ps.Core/Domain/UserGroup.cs`.
- External systems — MMPS, MF, ANSA, CNAM, CSP, banks, MGov gateway.

### 3.2 Functional requirements (TOR §2)

Authoritative running register: `TODO.md` (R0XXX series). Each R0XXX line is
the SRS clause; subsequent iteration notes link to the implementation.

Major functional clusters with pointers:

- Application intake and processing —
  `src/Cnas.Ps.Application/ApplicationProcessing/`.
- Pension benefit calculation — `src/Cnas.Ps.Application/Pension/` and
  `src/Cnas.Ps.Application/AthletePensions/`.
- Social benefits — `src/Cnas.Ps.Application/Benefits/`.
- Capitalised payments — `src/Cnas.Ps.Application/CapitalisedPayments/`.
- International agreements — `src/Cnas.Ps.Application/IntlAgreements/`.
- Payer declarations / ETL — `src/Cnas.Ps.Application/Etl/`,
  `src/Cnas.Ps.Application/Declarations/`.
- Personal account portal — `src/Cnas.Ps.Application/PersonalAccount/`.
- Helpdesk and notifications — `src/Cnas.Ps.Application/Helpdesk/`.
- Bulk operations + 4-eyes — `src/Cnas.Ps.Application/BulkActions/` (iter 81).

### 3.3 Non-functional requirements (TOR §3)

| Cluster | TOR codes | Implementation reference |
|---|---|---|
| Performance | PSR 001 → PSR 010 | [`docs/performance-kpis.md`](../performance-kpis.md), [`docs/performance-ops.md`](../performance-ops.md), [`docs/performance.md`](../performance.md) |
| Recovery | SEC + ARH | [`docs/recovery-procedures.md`](../recovery-procedures.md), [`docs/bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) |
| Architecture | ARH 001 → ARH 015 | [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md), [`docs/pm/sdd-iterative.md`](sdd-iterative.md) |
| Security | SEC 001 → SEC 020 | RBAC + ABAC (`Cnas.Ps.Application/Abac/`), 4-eyes (`BulkActions/`), audit chain (R0194) |
| Operations | OPR | [`docs/operations.md`](../operations.md), [`docs/production-deployment.md`](../production-deployment.md), [`docs/go-live-strategy.md`](../go-live-strategy.md) |

Gaps tracked in `TODO.md`. PSR coverage delta is in
[`docs/performance-kpis.md`](../performance-kpis.md) (R2176).

### 3.4 Integration points (TOR §4, Annex 4 operations)

External operations exposed and consumed:

- Inbound interop (other gov systems calling Protecția Socială):
  `src/Cnas.Ps.Application/Interop/IInteropApi.cs`,
  `src/Cnas.Ps.Api/Controllers/Interop/InteropController.cs` (iter 72).
- Offline batch ingest (Annex 4 large file flows):
  `src/Cnas.Ps.Application/Interop/Batch/` +
  `src/Cnas.Ps.Api/Controllers/Interop/OfflineBatchController.cs` (iter 79).
- Outbound facades (external MGov / agency APIs):
  `src/Cnas.Ps.Application/External/`.
- Integration status delta: [`docs/EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md).

### 3.5 Data model (TOR §5)

Authoritative model lives under `src/Cnas.Ps.Core/Domain/`. Aggregates are
EF-mapped via `src/Cnas.Ps.Infrastructure/Persistence/Configurations/`.
Read-side projections route through
`src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs` (iter 68/84).

## 4. Cadence / Lifecycle

- v0.1 issued at M1 close (this skeleton).
- Re-issued at end of each M2 iteration as new R0XXX requirements close.
- Frozen for UAT at M6 start.

## 5. Implementation map

Every R0XXX in `TODO.md` carries either an iteration completion note (with a
file path) or an explicit *pending* marker. The SRS does not duplicate that
register; it points to it.

## 6. Status

Skeleton complete. Section authoring is staggered: §3.1, §3.2, §3.4, §3.5 are
already substantively backed by the codebase; §3.3 is partially backed by
the docs listed above. Open gap: prose write-up of each NFR cluster against
each module — tracked by TODO R2402.

## 7. References

- `tor/TOR.md` §1–§5.
- `TODO.md` — running register (R0XXX functional + R2XXX programme).
- `docs/ARCHITECTURE.md`, `docs/pm/sdd-iterative.md`.
- `docs/performance-kpis.md`, `docs/performance-ops.md`,
  `docs/recovery-procedures.md`, `docs/bcp-drp-backup-plan.md`.
- `docs/EGOV-INTEGRATION-GAP.md`, `docs/operations.md`.
