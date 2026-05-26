# Bi-weekly Status Report — Template

> Anchored to TOR ID(s): R2413 (Task 2.4, Deliverable 2.5). Process artefact.
> One instance every two weeks; archived as
> `docs/pm/status-YYYY-MM-DD.md`.

## 1. Purpose

Concise, evidence-based status update to the Steering Committee. Single page
where possible. Decision-oriented: what is blocked, what needs a decision,
what is the trend on schedule / scope / risk / quality.

## 2. Scope

Two-week window. Covers all active milestones in parallel (M2 + M3 + M4 may
overlap). Excludes ad-hoc UX consultations — those live in the demo template
(R2412).

## 3. Sections

### 3.1 Header <!-- placeholder -->

- Reporting period: <!-- YYYY-MM-DD to YYYY-MM-DD -->
- Report no: <!-- sequential -->
- Author: <!-- Contractor PM -->
- Distribution: <!-- Steering Committee roster -->

### 3.2 Executive summary <!-- placeholder -->

- Overall RAG status: <!-- Green / Amber / Red -->
- One-paragraph narrative (3–4 sentences).

### 3.3 Milestone status <!-- placeholder -->

| Milestone | Plan % | Actual % | RAG | Comment |
|---|---|---|---|---|
| M1 — Preparation | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M2 — Design & Dev | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M3 — Integrations | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M4 — Migration | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M5 — Training | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M6 — Stabilisation | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| M7 — Support | <!-- --> | <!-- --> | <!-- --> | <!-- --> |

### 3.4 Deliverables completed this period <!-- placeholder -->

| TOR id | Deliverable | Evidence | Accepted by |
|---|---|---|---|
| <!-- R2XXX --> | <!-- name --> | <!-- file path or url --> | <!-- name + date --> |

### 3.5 Iteration progress (M2) <!-- placeholder -->

- Iteration #: <!-- -->
- Tests passing / total: <!-- e.g. 3905 / 3905 -->
- Build warnings: <!-- 0 -->
- Coverage delta: <!-- ratchet only goes up -->
- Top 3 R0XXX clauses closed: <!-- ids + one-liners -->

### 3.6 Risks and issues <!-- placeholder -->

| # | Description | Type | Severity | Owner | Mitigation | Trend |
|---|---|---|---|---|---|---|
| 1 | <!-- --> | <!-- risk/issue --> | <!-- P0–P3 --> | <!-- name --> | <!-- --> | <!-- ↑↓→ --> |

### 3.7 Change requests <!-- placeholder -->

| # | Title | Source | Impact (scope/time/cost) | Status |
|---|---|---|---|---|

### 3.8 Decisions requested <!-- placeholder -->

- <!-- list — one decision per bullet, with options + recommendation -->

### 3.9 Next two weeks <!-- placeholder -->

- <!-- top 5 commitments -->

### 3.10 Sign-off <!-- placeholder -->

- Contractor PM: <!-- name + date -->
- Beneficiary PMO: <!-- name + date -->

## 4. Cadence / Lifecycle

Bi-weekly across the entire programme (M1 → M7). Aggregated into a
milestone-close report at each gate. Archived in git permanently.

## 5. Implementation map

- Build / test numbers sourced from CI (`.github/workflows/ci.yml`).
- Deliverable evidence: cross-reference to `TODO.md` ids + file paths in
  `docs/` and `src/`.
- Coverage ratchet: `coverlet.runsettings`.

## 6. Status

Template ready for use. First periodic instance to be issued at the start of
the next reporting cycle. Tracked by TODO R2413.

## 7. References

- `docs/pm/project-management-plan.md` (R2401).
- `docs/pm/periodic-demo-template.md` (R2412).
- `docs/pm/suggestions-improvements-template.md` (R2416).
- `tor/TOR.md` §15, §16, §17.
