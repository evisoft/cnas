# Role — Șeful CNAS (SC)

The director-level role at the CNAS organisation level. Executive
oversight: institution-wide approvals, KPI / dashboard reading, sign-off
on high-impact actions. Few but high-value interactions.

## TOR identifier

- Code: **SC**.
- RBAC policy: `CnasDecider` (executive variant — same policy as SD,
  ABAC scope is institution-wide).
- ABAC: institution-wide read; approvals restricted to high-impact
  categories.
- Rate-limit partition: `Authenticated`.

## Use cases owned

- **UC09** — Extrag rapoarte (institution-wide).
- **UC10** — Aprob / resping (top-tier approvals only).

## Day-to-day tasks

- Approve high-value decisions, capitalised payments, recovery
  decisions where the threshold requires director sign-off.
- Read the KPI dashboard for institution-wide metrics.
- Review the access-rights report and the integrity-check findings.
- Counter-sign sensitive admin actions when AS-level isn't enough.

## Features they touch

- [`../features/decisions.md`](../features/decisions.md) (top-tier approvals)
- [`../features/reporting.md`](../features/reporting.md) (KPI dashboard)
- [`../features/audit.md`](../features/audit.md) (read-side)
- [`../features/admin-console.md`](../features/admin-console.md) (sign-off side)

## What they cannot do

- Day-to-day examination / verdict recording.
- Edit service-passport, workflow, template config — that is AS.
- Edit user accounts directly — operational HR / IT integration is
  external.

## Onboarding & offboarding

Provisioned through 4-eyes by two AS-level admins. SC accounts are
intentionally rare and are audited at every action by default
(`AuditCategory` carries a higher sensitivity flag).
