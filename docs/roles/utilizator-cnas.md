# Role — Utilizator CNAS (UCNAS)

CNAS staff member, typically an examiner or clerk in a regional
office. Day-to-day operator of the *Cerere → Examinare → Decizie →
Plată* lifecycle. The most active human role in the system.

## TOR identifier

- Code: **UCNAS**.
- RBAC policy: `CnasUser` (staff variant).
- ABAC: scoped to assigned subdivision(s) + document category set,
  optionally restricted by workflow-step ACL.
- Rate-limit partition: `Authenticated`.

## Use cases owned

- **UC03** — Caut / vizualizez (search + view).
- **UC04** — Dashboard.
- **UC05** — Execut sarcini.
- **UC06** — Depunere cerere (register on behalf of a citizen).
- **UC07** — Înregistrare formular.
- **UC08** — Examinare document (record verdicts).
- **UC09** — Extrag rapoarte (standard reports).
- **UC11** — Descarc document.
- **UC12** — Explorez registru (registries).
- **UC13** — Profil solicitant (read for casework).

## Day-to-day tasks

- Triage the personal task inbox (`/api/tasks/inbox`).
- Claim and complete `WorkflowTask` rows for assigned dossiers.
- Examine attached documents and record verdicts.
- Submit `ChangeRequest` rows when clarification is needed.
- Browse Plătitori / Persoane asigurate registries.
- Run standard Annex-6 reports for the assigned subdivision.
- Generate decision DOCX via Annex-7 templates.

## Features they touch

- [`../features/examination.md`](../features/examination.md)
- [`../features/applications.md`](../features/applications.md)
- [`../features/contributors.md`](../features/contributors.md)
- [`../features/insured-persons.md`](../features/insured-persons.md)
- [`../features/contributions.md`](../features/contributions.md)
- [`../features/reporting.md`](../features/reporting.md)
- [`../features/document-templates.md`](../features/document-templates.md)
- [`../features/notifications.md`](../features/notifications.md)
- [`../features/helpdesk.md`](../features/helpdesk.md)

## What they cannot do

- Issue final approvals or rejections — that requires **Șeful
  direcției** (`CnasDecider` policy).
- Edit service-passport configuration, workflow graphs, or document
  templates — that is **AS**.
- Touch user / permission admin.
- See data outside assigned subdivision (ABAC enforced).

## Onboarding & offboarding

- Onboarded by AS through 4-eyes — `POST /api/users` lands in
  `PendingAdminAction`, a second admin approves.
- Subdivision + group membership configured via
  `UserGroupsController` and `AccessScopeController`.
- Off-boarded by AS — `UserAccountState` transition Active →
  Disabled. All active sessions revoked. Open tasks reassigned by
  `IExaminerAssignmentService`.

Reference: [`../features/identity-access.md`](../features/identity-access.md).
