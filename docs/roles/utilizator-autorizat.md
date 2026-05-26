# Role — Utilizator autorizat (UA)

The "authorised user" — single shared CNAS-front-desk account used to
register paper applications on behalf of walk-in citizens who cannot
sign in via MPass themselves. Read access to public content and
write access to a narrow registration surface.

## TOR identifier

- Code: **UA**.
- RBAC policy: `CnasUser` (citizen-facing variant).
- ABAC: scoped to the staffed front-desk (subdivision).
- Rate-limit partition: `Authenticated`.

## Use cases owned

- **UC01** — Browse public content.
- **UC02** — Use the public calculators with citizen context.
- **UC09** — Extrag rapoarte (limited — only intake reports).
- **UC11** — Descarc document (download templates + blank forms).

## Day-to-day tasks

- Register paper applications brought in by walk-in citizens
  (`PaperFulfilmentController`).
- Print blank application forms in the citizen's preferred locale.
- Look up an existing application by reference number.

## Features they touch

- [`../features/public-portal.md`](../features/public-portal.md)
- [`../features/applications.md`](../features/applications.md) (paper-fulfilment endpoint)
- [`../features/document-templates.md`](../features/document-templates.md)

## What they cannot do

- Approve or record verdicts on an application — that is **UCNAS**.
- See data outside their assigned subdivision — ABAC filter.
- Edit personal data of a citizen — citizens edit their own profile,
  not the front-desk.

## Onboarding & offboarding

Created and managed by **Administrator de sistem** (AS) through the
4-eyes maker-checker queue. Account state lifecycle:
Active → Suspended (on leave) → Disabled (off-boarded). Force-revoke
sessions via `/api/sessions/{sqid}/revoke` if compromise suspected.

Reference: [`../features/identity-access.md`](../features/identity-access.md).

## Notes

SEC 014 reserves a `Local` scheme for this account (one shared
front-desk credential). The local username/password endpoint wiring
is **deferred** (R0051); today MPass remains the only working sign-in
path. Treat this role as documented but not fully wired pending the
local-auth completion.
