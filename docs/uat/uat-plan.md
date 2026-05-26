# UAT plan — supplier-led test types

> Anchored to TOR ID(s): R2450 (UAT 003, Milestone M6). Five supplier-led
> test types: unit, integration, performance (load + stress), recovery,
> security. Iteration 101. Companion to
> [`uat-joint-tests.md`](uat-joint-tests.md),
> [`coverage-policy.md`](coverage-policy.md) and
> [`acceptance-criteria.md`](acceptance-criteria.md).

## 1. Purpose / scope

Define the five supplier-led test categories required by UAT 003 for SI
„Protecția Socială", their entry/exit criteria, the existing in-repo
artefacts that anchor each category, and the residual gaps that must be
closed before the bilateral UAT window opens.

## 2. Audience / stakeholders

Supplier QA lead, supplier engineering lead, supplier security officer,
supplier SRE, CNAS QA observer, CNAS security officer, joint acceptance
committee.

## 3. Procedure (numbered)

### 3.1 Unit tests

1. Scope: domain logic, validators, services, mappers — single class or
   function in isolation per CLAUDE.md Phase 3 testing pyramid.
2. Anchored in `tests/Cnas.Ps.Core.Tests/` and
   `tests/Cnas.Ps.Application.Tests/` (validators, services), with
   targeted contract tests in `tests/Cnas.Ps.Contracts.Tests/` when DTO
   shapes carry rules.
3. Entry: green build with `-p:TreatWarningsAsErrors=true`. Exit: zero
   failing test cases on the supplier branch + CNAS clone.

### 3.2 Integration tests

1. Scope: multi-layer flows through DI + EF + Quartz + clock + outbound
   adapters using in-memory or test-container substitutes.
2. Anchored in `tests/Cnas.Ps.Infrastructure.Tests/` and
   `tests/Cnas.Ps.Api.Tests/`. Web smoke is in
   `tests/Cnas.Ps.Web.Tests/`, browser E2E is in
   `tests/Cnas.Ps.E2E.Tests/`.
3. Includes ABAC enforcement (iter 88) and 4-eyes++ workflow tests
   (`IChangeRequestService`, iter 81/94).
4. Exit: zero failing test cases; journey-style organisation per
   CLAUDE.md §3.3.

### 3.3 Performance tests (load + stress)

1. Scope: latency under sustained load (SLO p90 < 1 000 ms per
   `SloRegistry`) and behaviour past saturation.
2. Anchored in `perf/cnas-baseline.js` (k6) — the load harness wired to
   the architecture test
   `SloRegistryTests.PerfHarness_Declares_Default_P90_Threshold`.
3. Stress profile (>= 2x design load to discover knee): **R2705 — not yet
   committed**. Must be added before UAT window opens.
4. Exit: p90 / p95 thresholds met for steady load; documented knee
   point and graceful degradation under stress.

### 3.4 Recovery tests

1. Scope: data-loss, audit-chain break, and backup-target corruption.
2. Anchored in `docs/recovery-procedures.md` (iter 98) and the planned
   DR drill **R2708 — not yet committed**.
3. Exercise: restore from backup, verify SHA-256, replay audit hash
   chain via `IAuditChainVerifier`, confirm
   `AuditChainIntegrityCheck` (iter 95) reports green.
4. Exit: RTO <= 4 h + RPO <= 1 h (PLACEHOLDER — to be re-validated
   with CNAS during stabilization, per
   [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)).

### 3.5 Security tests

1. Scope: authentication, ABAC authorization (iter 88), audit-log hash
   chain (R0194 / SEC 047), 4-eyes++ change workflow (iter 81/94),
   injection prevention, file upload (magic bytes), token hashing,
   account enumeration prevention.
2. Anchored in `tests/Cnas.Ps.Architecture.Tests/` (boundary +
   contract + read-replica layering rules) plus security-flavoured unit
   and integration tests in `Application.Tests` /
   `Infrastructure.Tests`.
3. Outputs: SAST scan report (CI stage 6 per CLAUDE.md §4.1) and a
   reproducible runbook for each finding.
4. Exit: zero Critical + zero High SAST findings open at sign-off.

## 4. Acceptance criteria / sign-off

- Each of the five categories has a written report (results + log + tool
  versions) signed by the supplier QA lead.
- Failing tests count = 0 at sign-off.
- Coverage gate met per [`coverage-policy.md`](coverage-policy.md).
- Defect bar met per [`acceptance-criteria.md`](acceptance-criteria.md).
- Reports archived against the active `MigrationPlan` version and the
  corresponding `ChangeRequest` (iter 94).

## 5. Implementation map

| Test type | Where (repo) |
|---|---|
| Unit | `tests/Cnas.Ps.Core.Tests/`, `tests/Cnas.Ps.Application.Tests/` |
| Integration | `tests/Cnas.Ps.Infrastructure.Tests/`, `tests/Cnas.Ps.Api.Tests/`, `tests/Cnas.Ps.Web.Tests/`, `tests/Cnas.Ps.E2E.Tests/` |
| Performance | `perf/cnas-baseline.js` (k6), `src/Cnas.Ps.Core/Performance/SloRegistry.cs` |
| Recovery | `docs/recovery-procedures.md`, `docs/bcp-drp-backup-plan.md` |
| Security | `tests/Cnas.Ps.Architecture.Tests/`, audit chain (`IAuditChainVerifier`), `AuditChainIntegrityCheck` |

## 6. Status / open gaps

- R2705 — k6 stress profile not yet committed.
- R2708 — DR drill scheduling not yet committed (runbook stub in
  `docs/dr/dr-drill-runbook.md`).
- Bilateral UAT environment provisioning — pending.
- SAST baseline + waiver list — pending.

## 7. References

- TOR §UAT 003
- TODO.md row R2450
- [`uat-joint-tests.md`](uat-joint-tests.md)
- [`coverage-policy.md`](coverage-policy.md)
- [`acceptance-criteria.md`](acceptance-criteria.md)
- [`../recovery-procedures.md`](../recovery-procedures.md)
- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)
