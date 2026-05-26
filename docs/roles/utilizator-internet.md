# Role — Utilizator Internet (UI)

Anonymous internet visitor. Not authenticated. Reads public content
and uses the public calculators.

## TOR identifier

- Code: **UI**.
- RBAC policy: none (anonymous).
- ABAC: none.
- Rate-limit partition: `Anonymous` (5 req / 60 s per resolved IP).
- CAPTCHA: required on every endpoint they hit
  (`PublicController` is class-level `[RequireCaptcha]`).

## Use cases owned

- **UC01** — Explorez conținutul portalului (browse public content).
- **UC02** — Servicii informative (eligibility calculators).

## Day-to-day tasks

- Browse the catalogue of life-event services (which benefit applies to me?).
- Read multi-locale help topics (RO / RU / EN).
- Calculate retirement age based on birth date + sex + contribution
  history hints.
- Check application status by reference number.
- Download blank application templates (read-only).

## Features they touch

- [`../features/public-portal.md`](../features/public-portal.md)
- [`../features/document-templates.md`](../features/document-templates.md) (blank templates only)

## What they cannot do

- Submit applications — must authenticate via MPass first
  (becomes Solicitant).
- See personal data — every personal-account endpoint requires
  authentication.
- Trigger any state change in the system other than the side-effect of
  the CAPTCHA token consumption.

## Onboarding & offboarding

Not applicable — anonymous. The "transition" is signing in through
MPass, which promotes them to **Solicitant** (SOL) for citizens or
**Utilizator CNAS** (UCNAS) for staff.
