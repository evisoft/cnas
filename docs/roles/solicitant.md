# Role — Solicitant (SOL)

A citizen of Moldova who has signed in via MPass and is acting on
their own behalf (or via MPower delegation, on behalf of someone else).
Submits applications, tracks their progress, downloads decisions, and
views their personal cabinet.

## TOR identifier

- Code: **SOL**.
- RBAC policy: `CnasUser` (applicant variant).
- ABAC: scoped to "own data" — every read filters by IDNP / IDNO
  derived from the MPass assertion, with optional MPower delegation
  expanding scope to the principal-IDNP.
- Rate-limit partition: `Authenticated`. Upload partition for
  attachments.

## Use cases owned

- **UC01** — Browse public content (carried over from anonymous).
- **UC02** — Use eligibility calculators.
- **UC06** — Depunere cerere (submit application).
- **UC11** — Descarc document (download own decisions, certificates).
- **UC13** — Profil solicitant (personal cabinet).

## Day-to-day tasks

- Submit a new application for any of the 81 life-event services.
- Upload supporting attachments (magic-byte validated, ≤ 25 MiB).
- Track application status in the personal cabinet.
- Respond to clarification requests from examiners (ChangeRequest).
- Download signed decision documents.
- View benefit-payment history (own).
- Manage own profile (phone, IBAN, language).
- Manage own sessions (`/api/sessions`).

## Features they touch

- [`../features/public-portal.md`](../features/public-portal.md)
- [`../features/applications.md`](../features/applications.md)
- [`../features/personal-account.md`](../features/personal-account.md)
- [`../features/document-templates.md`](../features/document-templates.md) (own documents)
- [`../features/notifications.md`](../features/notifications.md) (own inbox)
- [`../features/payments.md`](../features/payments.md) (own payments)

## What they cannot do

- See data belonging to anyone else, unless an MPower delegation
  expands their scope to a specific principal-IDNP.
- Approve or examine — that is **UCNAS** / **SD** territory.
- Touch any admin / config surface.

## MPower delegation

MPower is consumed via SAML attributes inside the MPass assertion
(`mpower:principal_idnp`, `mpower:delegation_id`). It is **not** a
separate HTTP service. When present, the parser populates
`ICallerContext.DelegationPowerId` and the principal-IDNP, which the
ABAC scope filter then uses to widen the read scope.

Delegation lifecycle UI (R0057) is deferred — the claim path works,
the admin UI to grant / revoke is not built.

## Onboarding & offboarding

No onboarding step in SI PS itself — sign-in via MPass auto-creates
the local profile shadow on first hit. Off-boarding is implicit:
the citizen's MPass account state controls access.

GDPR removal — handled through the 4-eyes admin queue
([`../features/admin-console.md`](../features/admin-console.md)).
