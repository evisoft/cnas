# Suggestions / Improvements Report — Template

> Anchored to TOR ID(s): R2416 (Deliverable 2.3, Milestone M2). One instance
> per milestone close (or earlier if material findings accumulate); archived
> as `docs/pm/improvements-YYYY-MM-DD.md`.

## 1. Purpose

Roll up the change requests, UX findings, and operator suggestions gathered
during demos (R2412) and routine operations into a single Beneficiary-facing
document. Each entry either becomes a scoped change request (PMP §scope and
change control) or is explicitly closed without action with a stated reason.

## 2. Scope

Covers suggestions originating from:

- UX consultations at periodic demos.
- Operator feedback (CNAS clerks, payer support staff).
- Beneficiary representatives.
- Steering Committee directives.
- Internal Contractor observations (engineering / QA / security).

Excludes defects against accepted scope — those follow the bug-fix workflow
and appear in the bi-weekly status report (R2413).

## 3. Sections

### 3.1 Header <!-- placeholder -->

- Report period: <!-- start → end -->
- Author: <!-- Contractor PM -->
- Reviewer: <!-- Beneficiary PMO -->
- Distribution: <!-- Steering Committee -->

### 3.2 Summary by category <!-- placeholder -->

| Category | New | Accepted | Rejected | Carried over |
|---|---|---|---|---|
| UX / workflow | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Performance | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Security | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Integration | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Data / reporting | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Operations | <!-- --> | <!-- --> | <!-- --> | <!-- --> |
| Other | <!-- --> | <!-- --> | <!-- --> | <!-- --> |

### 3.3 Detailed register <!-- placeholder -->

| # | Origin | Date | Category | Description | Linked TOR / TODO id | Recommendation | Estimated impact | Decision | Decision owner |
|---|---|---|---|---|---|---|---|---|---|
| 1 | <!-- demo M2-Iter-X --> | <!-- YYYY-MM-DD --> | UX | <!-- e.g. simplify intake step 3 --> | <!-- R0XXX or new --> | Accept / Reject / Defer | Time + cost + risk | <!-- --> | <!-- name --> |

### 3.4 Accepted change requests <!-- placeholder -->

For each accepted item, capture:

- Scoped CR identifier: <!-- e.g. CR-2026-001 -->
- Linked TODO line(s) opened in `TODO.md`.
- Target milestone / iteration.
- Acceptance criteria.

### 3.5 Rejected suggestions <!-- placeholder -->

For each rejected item:

- Reason (out of scope / risk / cost / contradicts TOR clause X).
- Whether the originator was informed.
- Decision date and owner.

### 3.6 Carry-overs <!-- placeholder -->

- Items still under analysis at report close.

### 3.7 Trend commentary <!-- placeholder -->

- Themes observed (e.g. repeated requests on a single workflow).
- Recommended programme-level adjustments (e.g. additional UX consultation,
  extra performance hardening iteration).

### 3.8 Sign-off <!-- placeholder -->

- Contractor PM: <!-- name + date -->
- Beneficiary PMO: <!-- name + date -->

## 4. Cadence / Lifecycle

Issued at every milestone close (and ad hoc when a Steering Committee
requests one). Accepted items flow into `TODO.md` as new R-coded lines and
appear in subsequent status reports (R2413).

## 5. Implementation map

- Source feedback artefacts: demo minutes
  (`docs/pm/demo-YYYY-MM-DD.md`, R2412).
- Outbound CRs: new lines in `TODO.md`, owned by the relevant module
  (e.g. `src/Cnas.Ps.Application/<module>`).
- Status reflection: each next status report (R2413) lists open CRs.

## 6. Status

Template ready for use. First instance to be authored at the close of the
next M2 iteration window or earlier if material findings accumulate. Tracked
by TODO R2416.

## 7. References

- `docs/pm/project-management-plan.md` (R2401).
- `docs/pm/periodic-demo-template.md` (R2412).
- `docs/pm/status-report-template.md` (R2413).
- `tor/TOR.md` §16 (M2 deliverables).
