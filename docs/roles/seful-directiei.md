# Role — Șeful direcției (SD)

Head of a regional / subject-matter department within CNAS. Approves
or rejects examined applications, monitors departmental SLA, and
runs cross-team reports for the units they own.

## TOR identifier

- Code: **SD**.
- RBAC policy: `CnasDecider` (passes `CnasUser` transparently).
- ABAC: scoped to the led department(s) — wider than UCNAS, narrower
  than CnasAdmin.
- Rate-limit partition: `Authenticated`.

## Use cases owned

- **UC08** — Examinare document (oversight + override).
- **UC09** — Extrag rapoarte (departmental scope).
- **UC10** — Aprob / resping.

## Day-to-day tasks

- Approve or reject examined applications via
  `/api/approvals/{sqid}/approve` / `…/reject`.
- Reassign tasks across team members when load-balancing breaks
  (audited).
- Monitor SLA and overdue dossiers (`DossierSlaMonitorJob` outputs).
- Run departmental Annex-6 reports.
- Resolve cross-team escalations.

## Features they touch

- [`../features/examination.md`](../features/examination.md) (approvals)
- [`../features/decisions.md`](../features/decisions.md) (decision sign-off)
- [`../features/reporting.md`](../features/reporting.md)
- [`../features/notifications.md`](../features/notifications.md)
- [`../features/workflows.md`](../features/workflows.md) (visibility, not editing)

## What they cannot do

- Edit service-passport / workflow / template configuration — that is
  **AS**.
- Manage users or permissions outside their scope — limited
  permission edits go through 4-eyes if at all.
- Bypass the 4-eyes maker-checker queue on destructive actions.

## Onboarding & offboarding

Same lifecycle as UCNAS (created by AS through 4-eyes). The
difference is the role-set membership — `CnasDecider` policy plus the
explicit ABAC scope for the department(s) led.

Reference: [`../features/identity-access.md`](../features/identity-access.md).
