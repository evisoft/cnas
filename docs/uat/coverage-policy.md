# UAT coverage policy

> Anchored to TOR ID(s): R2452 (UAT 005, Milestone M6). Iteration 101.
> Companion to [`uat-plan.md`](uat-plan.md) and
> [`acceptance-criteria.md`](acceptance-criteria.md).

## 1. Purpose / scope

Defines the >= 90% test coverage requirement for UAT 005 and how it is
measured against the existing test suite. Identifies what counts toward
coverage today, what counts toward it after the next CI step lands, and
which structural quality bars complement raw line coverage.

## 2. Audience / stakeholders

Supplier QA lead, supplier engineering lead, CNAS QA observer, joint
acceptance committee.

## 3. Procedure (numbered)

### 3.1 Current baseline

1. The build is run with `dotnet test Cnas.Ps.slnx --nologo`.
2. Every test project listed below is required to be green at sign-off
   time and is gated by `-p:TreatWarningsAsErrors=true`.

| Project | Counts toward |
|---|---|
| `tests/Cnas.Ps.Core.Tests/` | Domain logic |
| `tests/Cnas.Ps.Application.Tests/` | Services + validators |
| `tests/Cnas.Ps.Contracts.Tests/` | DTO contracts |
| `tests/Cnas.Ps.Infrastructure.Tests/` | EF + jobs + adapters |
| `tests/Cnas.Ps.Api.Tests/` | Controllers + middleware |
| `tests/Cnas.Ps.Web.Tests/` | Razor pages smoke |
| `tests/Cnas.Ps.E2E.Tests/` | End-to-end browser flows |
| `tests/Cnas.Ps.Architecture.Tests/` | Structural rules (47/47 must pass) |
| `tests/Cnas.Ps.Accessibility.Tests/` | a11y theory rows + axe-core |

### 3.2 Architecture + naming + ratchet rules

1. `tests/Cnas.Ps.Architecture.Tests/` enforces layer boundaries,
   naming conventions, contract rules, time-provider usage,
   read-replica layering, the SLO registry contract, and the
   external-ID Sqid rule (CLAUDE.md RULE 3).
2. The suite reports **47/47** structural tests passing at the iteration
   100 baseline. New violations either fix the code or are added to
   the explicit grandfather list (Ratchet pattern, CLAUDE.md
   cross-cutting principles).

### 3.3 Accessibility theory rows

1. `tests/Cnas.Ps.Accessibility.Tests/` uses xUnit `Theory` rows for
   every public route in `PublicRouteCatalogTests` and the response
   smoke matrix in `ResponsiveSmokeTests`.
2. The axe-core runner (`AxeRunner`) is exercised end-to-end against
   each public page; placeholder rows fail loudly if the axe bundle
   is missing (`AxeBundleMissingException`).

### 3.4 Line coverage instrumentation

1. The repository ships `coverlet.runsettings` ready for use with
   `dotnet test --collect:"XPlat Code Coverage"`.
2. **Future CI step:** coverage collection + 90% gate wiring is the
   delta required to lift this policy from "structurally enforced" to
   "numerically enforced". Tracked separately; line-coverage gating is
   not yet wired into the CI pipeline.
3. Until that step lands, the 90% target is met by counting passing
   tests across the projects in §3.1 plus the 47/47 architecture
   tests plus the accessibility theory matrix; raw line-coverage
   numbers are reported by supplier QA from local
   `coverlet.runsettings` runs.

## 4. Acceptance criteria / sign-off

- All test projects in §3.1 green.
- 47/47 architecture-test pass count maintained (Ratchet).
- Accessibility theory matrix executes end-to-end against the staging
  build with the axe bundle present.
- Supplier QA produces a coverlet HTML report covering the supplier
  branch; CNAS QA archives the report next to the UAT sign-off.
- After the future CI step lands, the gate switches to "fail if line
  coverage < 90% on changed code" per CLAUDE.md §3.4.

## 5. Implementation map

| Capability | Where |
|---|---|
| Coverlet settings | `coverlet.runsettings` |
| Architecture rules | `tests/Cnas.Ps.Architecture.Tests/` |
| Accessibility runner | `tests/Cnas.Ps.Accessibility.Tests/AxeRunner.cs` |
| Public route catalogue | `tests/Cnas.Ps.Accessibility.Tests/PublicRouteCatalogTests.cs` |
| Responsive smoke matrix | `tests/Cnas.Ps.Accessibility.Tests/ResponsiveSmokeTests.cs` |
| Build / test entry point | `Cnas.Ps.slnx` |

## 6. Status / open gaps

- Coverlet collection + 90% CI gate — pending future CI step (tracked
  separately from this iteration).
- Branch-coverage policy — not yet decided; iteration 101 adopts line
  coverage only.
- Per-project coverage thresholds — not yet declared; supplier-wide
  90% target applies uniformly today.

## 7. References

- TOR §UAT 005
- TODO.md row R2452
- CLAUDE.md §3 Testing Strategy, §3.4 Coverage Gates
- [`uat-plan.md`](uat-plan.md)
- [`acceptance-criteria.md`](acceptance-criteria.md)
