# Role — Administrator de sistem (AS)

Functional / business administrator inside CNAS. Owns the
configuration surface: service passports, workflow graphs, document
templates, classifier catalogues, user accounts, permission
assignments, notification strategies. Does NOT touch
infrastructure / cluster level — that is AT.

## TOR identifier

- Code: **AS**.
- RBAC policy: `CnasAdmin` (passes `CnasDecider` and `CnasUser`
  transparently).
- ABAC: institution-wide for config; specific scope for permission
  grants.
- Rate-limit partition: `Authenticated`.
- All destructive AS actions route through the 4-eyes maker-checker
  queue (R0058).

## Use cases owned

- **UC15** — Configurez serviciu (service passport).
- **UC16** — Configurez flux (workflow definitions).
- **UC17** — Metadate & șabloane (template admin).
- **UC18** — Utilizatori & acces.
- **UC20** — Proceduri automate (functional side).

## Day-to-day tasks

- Add or edit a service passport when a new life-event service is
  introduced (or rules change).
- Publish a new workflow graph version (pinned — only new instances
  pick it up).
- Upload Annex-7 DOCX templates; manage RO / RU / EN language
  coverage.
- Create or off-board CNAS staff accounts.
- Configure user groups, ABAC rules, granular permissions.
- Edit MNotify notification templates and per-workflow-step strategies.
- Manage classifier catalogues + voucher quotas.
- Review the failed-job DLQ and replay where appropriate.

## Features they touch

- [`../features/service-passport.md`](../features/service-passport.md)
- [`../features/workflows.md`](../features/workflows.md)
- [`../features/document-templates.md`](../features/document-templates.md)
- [`../features/identity-access.md`](../features/identity-access.md)
- [`../features/notifications.md`](../features/notifications.md)
- [`../features/admin-console.md`](../features/admin-console.md)
- [`../features/background-jobs.md`](../features/background-jobs.md) (functional)

## What they cannot do

- Cluster / Kubernetes / database operations — that is AT.
- Bypass 4-eyes — destructive actions queue as
  `PendingAdminAction` and require a second approver.
- View raw plaintext of encrypted fields without an
  `AuditFieldPolicy` allowing the access — every decrypt is audited.

## Onboarding & offboarding

AS-level accounts are created through 4-eyes by two existing
AS-level admins (chicken-and-egg bootstrap is handled out-of-band
during initial deployment via a seed script). Off-boarding revokes
the role and force-revokes all sessions; pending 4-eyes actions
owned by the off-boarded user are auto-expired by
`MakerCheckerExpirySweeper`.

Reference: [`../features/identity-access.md`](../features/identity-access.md).
