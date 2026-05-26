# DEL 005 — Acceptance testing deliverables (index)

> Anchored to TOR ID(s): R2604 (TOR §7.1). Index doc; content lives
> in the linked files. Iteration 103.

## 1. Purpose

Single navigation surface for the acceptance-testing deliverables
required by TOR §7.1 DEL 005. Bundles the UAT plan, joint test
matrix, coverage policy, acceptance criteria and the final acceptance
protocol template.

## 2. Audience

CNAS Service Owner, supplier QA lead, joint stakeholder UAT panel,
acceptance committee, audit reviewers.

## 3. Bundle contents

| Artefact | File | TOR row | Status |
|---|---|---|---|
| UAT plan — 5 supplier-led test types | [`../uat/uat-plan.md`](../uat/uat-plan.md) | R2450 / UAT 003 | Iter 101 |
| UAT — 3 joint test types | [`../uat/uat-joint-tests.md`](../uat/uat-joint-tests.md) | R2451 / UAT 004 | Iter 101 |
| Coverage policy (≥ 90% target) | [`../uat/coverage-policy.md`](../uat/coverage-policy.md) | R2452 / UAT 005 | Iter 101 |
| Acceptance criteria (0 critical / <3 major) | [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md) | R2453 / UAT 006 | Iter 101 |
| Acceptance protocol template | [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md) | R2456 / COM 004 | Iter 101 |
| Stabilization plan (3-month) | [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md) | R2457 / STAB 001 | Iter 101 |
| Final acceptance criteria (post-stabilization) | [`../stabilization/final-acceptance.md`](../stabilization/final-acceptance.md) | R2458 / STAB 004 | Iter 101 |

## 4. Test-type coverage matrix

| Test type | Source of truth | Acceptance gate |
|---|---|---|
| Unit | 8 `Cnas.Ps.*.Tests` projects | R2701 — 0 failures |
| Integration | Same projects, integration suites | R2701 |
| Performance (load + stress) | `perf/cnas-baseline.js` (k6) + `SloRegistry` | R2705 (open) |
| Recovery | `docs/recovery-procedures.md` + R2708 drill | R2708 (open) |
| Security | Audit chain + ABAC + 4-eyes++ | R0194 / iter 88 / iter 81/94 |
| Usability (joint) | UAT joint-test matrix | UAT 004 |
| Functional (joint) | UAT joint-test matrix | UAT 004 |
| Acceptance (joint) | Acceptance protocol template | UAT 006 / COM 004 |

## 5. Acceptance criteria

- All linked artefacts exist and resolve.
- Coverage ≥ 90% (currently ratcheting from 80% — R2702 open).
- Bug-bar at sign-off: `critical_open == 0 AND major_open < 3`
  (R2453).
- Acceptance Protocol signed (R2456 / COM 004).
- Stabilization gate met before final sign-off (R2458 / STAB 004).

## 6. Status / open gaps

- R2705 k6 perf suite: not yet committed.
- R2708 quarterly DR drill: not yet executed.
- R2702 coverage ratchet to 90%: CI gate currently at 80%.
- Acceptance Protocol signatures: pending UAT completion.

## 7. References

- TOR §7.1 DEL 005, §UAT 003-006, §COM 004, §STAB 001 / STAB 004
- TODO.md R2604 (this row), R2450-R2458
- [`../uat/`](../uat/), [`../acceptance/`](../acceptance/),
  [`../stabilization/`](../stabilization/)
