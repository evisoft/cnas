# UAT plan — joint test types

> Anchored to TOR ID(s): R2451 (UAT 004, Milestone M6). Three joint
> test types: usability, functional, acceptance. Iteration 101.
> Companion to [`uat-plan.md`](uat-plan.md),
> [`coverage-policy.md`](coverage-policy.md) and
> [`acceptance-criteria.md`](acceptance-criteria.md).

## 1. Purpose / scope

Define the three jointly-executed test types required by UAT 004,
the stakeholders that must be present on both sides, the execution
schedule, and the sign-off requirements. The supplier executes; CNAS
witnesses, validates, and signs off in real time.

## 2. Audience / stakeholders

| Role | Supplier side | CNAS side |
|---|---|---|
| Test lead | Supplier QA lead | CNAS QA lead |
| Functional witness | Solution architect | CNAS business owner (per benefit type) |
| Usability witness | UX lead | CNAS end-user representatives (counter staff) |
| Acceptance signatory | Project manager | CNAS project director |
| Accessibility witness | Frontend lead | CNAS accessibility officer |
| Security witness | Security officer | CNAS security officer |

## 3. Procedure (numbered)

### 3.1 Usability tests

1. Scope: navigation, task completion times, comprehension, RO + RU
   language switching, accessibility (axe-core checks from
   `tests/Cnas.Ps.Accessibility.Tests/`).
2. Method: moderated task-based sessions with representatives of each
   counter-staff cohort (per `docs/training/training-plan.md` audience
   tiers). Five tasks minimum per benefit family.
3. Pass criterion: >= 80% task completion, <= 2 critical comprehension
   issues per session.

### 3.2 Functional tests

1. Scope: every benefit type end-to-end (Cerere intake -> ABAC
   authorisation -> decision -> MSign -> MNotify -> payment).
2. Inputs anchored to seeded fixtures used by
   `tests/Cnas.Ps.E2E.Tests/`. Joint sessions execute scripted
   scenarios; supplier QA replays the same scenarios with synthetic
   data while CNAS witnesses sign each scenario record.
3. Includes 4-eyes++ workflow walk-throughs (`IChangeRequestService`,
   iter 81/94) and audit-chain verification
   (`IAuditChainVerifier`, R0194).
4. Pass criterion: every scripted scenario reaches the expected
   terminal state; audit trail emits the documented event codes.

### 3.3 Acceptance tests

1. Scope: business acceptance against the SRS + SDD (`docs/pm/`),
   verifying that every TOR row tagged for M6 is demonstrably
   implemented or formally waived.
2. Method: scenario-based walk-throughs derived from the
   `docs/pm/srs-structural.md` test items. Each scenario maps to one
   or more TOR rows.
3. Pass criterion: defect bar in
   [`acceptance-criteria.md`](acceptance-criteria.md) met, no
   integrity-check findings open above the threshold defined in
   [`../stabilization/final-acceptance.md`](../stabilization/final-acceptance.md).

### 3.4 Execution schedule (indicative)

| Week | Track | Mode |
|---|---|---|
| W1 | Usability cohort 1 (RO), Functional batch A | Classroom + staging |
| W2 | Usability cohort 2 (RU), Functional batch B | Classroom + staging |
| W3 | Acceptance walk-through (Part 1), defect triage | Joint board room |
| W4 | Acceptance walk-through (Part 2), final sign-off | Joint board room |

Scheduled against the M6 stabilization window
([`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md))
and the `MaintenanceWindow` Major-notice cadence (iter 94).

## 4. Acceptance criteria / sign-off

- Every scenario record signed by the matching witness on both sides.
- Per-track exit report signed by both test leads.
- Acceptance Protocol template
  ([`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md))
  fully populated and counter-signed by both project leaders.
- Outstanding defects rolled into the M6 stabilization tracker
  (`SupportTicket`, iter 92) at appropriate severity.

## 5. Implementation map

| Track | Where (repo) |
|---|---|
| Usability | `tests/Cnas.Ps.Accessibility.Tests/`, `tests/Cnas.Ps.Web.Tests/`, `docs/design-system.md` |
| Functional | `tests/Cnas.Ps.E2E.Tests/`, `tests/Cnas.Ps.Api.Tests/`, ABAC (`IAbacRuleRegistryService`), `IChangeRequestService` |
| Acceptance | `docs/pm/srs-structural.md`, `docs/pm/sdd-iterative.md`, this directory |
| Sign-off template | [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md) |

## 6. Status / open gaps

- Bilateral UAT environment provisioning — pending.
- Cohort selection for usability tests — pending CNAS HR confirmation.
- Recording / video archival policy — pending.
- Translation of acceptance scenarios to RU — pending.

## 7. References

- TOR §UAT 004
- TODO.md row R2451
- [`uat-plan.md`](uat-plan.md)
- [`coverage-policy.md`](coverage-policy.md)
- [`acceptance-criteria.md`](acceptance-criteria.md)
- [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)
- [`../training/training-plan.md`](../training/training-plan.md)
