# Project Management Plan (PMP)

> Anchored to TOR ID(s): R2401 (Task 1.2, Deliverables 1.1 + 1.2). Living
> document — updated at each milestone boundary. Version 0.1, iteration 99.

## 1. Purpose

Single source of truth for *how* the SI „Protecția Socială" programme is run:
governance, schedule, resources, scope control, risk, quality, and
communications. Combines TOR Deliverable 1.1 (project management plan) and
Deliverable 1.2 (detailed schedule + resources).

## 2. Scope

Covers all 7 TOR milestones (M1 → M7). Excludes operational runbooks
(see `docs/operations.md`) and contractual matters (Beneficiary / Procurement).

## 3. Content / Sections

The PMP MUST contain the following sections. Sections marked *(skeleton)* are
intentionally stubbed and tracked by named TODO ids.

1. Governance — Steering Committee, Working Group, escalation matrix.
2. Schedule — Gantt by milestone (TOR §16). M1: 6mo, M2: 20mo, M3: 5mo (parallel
   M2), M4: 6mo, M5: 1mo, M6: 3mo, M7: 12mo support.
3. Resource plan — roles per TOR §15.2 (PM, BA, Architect, Lead Dev, QA, DBA,
   Security, UX, Trainer).
4. Scope and change control — change requests through the Steering Committee;
   technical CRs through the iteration backlog (`TODO.md`).
5. Risk register — initial register *(skeleton — owner: PM; due M1 end)*.
6. Quality management — TDD discipline (CLAUDE.md cardinal rule 1), warnings
   as errors, ratchet coverage, CI gates (`.github/workflows/ci.yml`).
7. Communications plan — bi-weekly status (R2413), monthly demo (R2412),
   ad-hoc UX consultations, Steering Committee monthly.
8. Procurement and dependencies — MCloud environments (R2404), MGov shared
   services (R2420), MConnect contracts (R2421), FMS (R2422).
9. Deliverables register — mirrors TOR §16; cross-referenced to `TODO.md`.
10. Acceptance criteria — per deliverable; matches §17 acceptance protocol.
11. Documentation plan — list of docs under `docs/`, with owners and cadence.
12. Training plan — pointer to R2440-R2443 series and Milestone M5.

## 4. Cadence / Lifecycle

- Version 0.x during M1.
- Version 1.0 at end of M1 (signed by Beneficiary).
- Patch release (1.x) at each milestone gate. Re-baselined only on Steering
  Committee approval.
- Diffs tracked in git history of this file.

## 5. Implementation map

- Schedule artefact: TOR §16 (mermaid gantt baseline). Detailed schedule lives
  in `TODO.md` as the running register.
- Coverage and quality gates: `coverlet.runsettings`, `Directory.Build.props`,
  CI workflow.
- Deliverable evidence: cross-referenced inline against `TODO.md` ids.

## 6. Status

Skeleton only. Sections 1, 5, 7, 9, 10 require Beneficiary input before they
can be authored end-to-end. Owner: Contractor PM. Tracked by TODO R2401.

## 7. References

- `tor/TOR.md` §15 (project organisation), §16 (milestones), §17 (acceptance).
- `docs/pm/project-kickoff.md` (R2400) — kick-off deliverable.
- `docs/pm/srs-structural.md` (R2402) — SRS skeleton.
- `docs/pm/tech-infra-requirements.md` (R2403).
- `docs/pm/sdd-iterative.md` (R2414) — iterative SDD.
- `docs/pm/status-report-template.md` (R2413).
- `docs/pm/periodic-demo-template.md` (R2412).
- `docs/operations.md`, `docs/go-live-strategy.md`, `docs/production-deployment.md`.
