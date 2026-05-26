# Periodic Demo + UX Consultation — Template

> Anchored to TOR ID(s): R2412 (Task 2.3, Milestone M2). Process artefact.
> One instance per iteration; archived as
> `docs/pm/demo-YYYY-MM-DD.md`.

## 1. Purpose

Capture the outcome of the per-iteration demo and the UX consultation that
accompanies it. Feeds the suggestions / improvements register
(`docs/pm/suggestions-improvements-template.md`, R2416) and the bi-weekly
status report (R2413).

## 2. Scope

Single iteration. Excludes Steering Committee level decisions (Steering uses
the status report).

## 3. Sections

### 3.1 Header <!-- placeholder -->

- Iteration: <!-- e.g. M2-Iter-12 -->
- Date: <!-- YYYY-MM-DD -->
- Demo lead: <!-- name + role -->
- Attendees (Beneficiary): <!-- list -->
- Attendees (Contractor): <!-- list -->
- Attendees (UX panel): <!-- list of role representatives — Solicitant proxy, CNAS clerk, payer rep -->
- Recording / minutes link: <!-- url -->

### 3.2 Iteration scope demonstrated <!-- placeholder -->

- Iteration goals (from PMP): <!-- bullet list -->
- TOR clauses (R0XXX) demonstrated: <!-- ids -->
- Build / environment URL: <!-- dev or test URL -->

### 3.3 Demo agenda <!-- placeholder -->

| # | Feature | TOR id | Driver | Duration | Outcome |
|---|---|---|---|---|---|
| 1 | <!-- e.g. Solicitant intake --> | R00XX | <!-- name --> | <!-- min --> | <!-- accepted / change requested --> |
| 2 | ... | ... | ... | ... | ... |

### 3.4 UX consultation <!-- placeholder -->

For each demonstrated feature, capture:

- User journey reviewed: <!-- description -->
- Persona representing it: <!-- Solicitant / CNAS clerk / payer / auditor -->
- Findings (positive): <!-- list -->
- Findings (issues, with severity P0–P3): <!-- list -->
- Accessibility / clarity notes: <!-- list -->

### 3.5 Action register <!-- placeholder -->

| # | Action | Owner | Due iteration | Linked TODO id |
|---|---|---|---|---|
| 1 | <!-- e.g. revise wording on field X --> | <!-- name --> | <!-- M2-Iter-13 --> | <!-- R0XXX or new --> |

### 3.6 Decisions <!-- placeholder -->

| # | Decision | Made by | Rationale | Reversible? |
|---|---|---|---|---|

### 3.7 Carry-overs to next iteration <!-- placeholder -->

- <!-- list -->

### 3.8 Sign-off <!-- placeholder -->

- Beneficiary representative: <!-- name + signature/date -->
- Contractor PM: <!-- name + signature/date -->

## 4. Cadence / Lifecycle

One demo per M2 iteration (TOR R2410 — 1-month iterations). Each instance is
date-stamped and committed to git. Aggregate findings roll up into the
suggestions register (R2416) at milestone close.

## 5. Implementation map

- Iteration evidence: git history under `src/` and `tests/` between two
  iteration tags.
- Demo recordings: stored outside the repository per Beneficiary policy.
- Linked TODO entries: line-edited in `TODO.md` against the relevant R0XXX.

## 6. Status

Template ready for use. First instance to be authored at the start of the
next demo cycle. Tracked by TODO R2412.

## 7. References

- `docs/pm/project-management-plan.md` (R2401) — governance.
- `docs/pm/status-report-template.md` (R2413).
- `docs/pm/suggestions-improvements-template.md` (R2416).
- `tor/TOR.md` §15, §16.
