# Feature — Personal account (citizen / Solicitant)

## What it is

The citizen-facing personal cabinet inside SI PS. Lets the authenticated
applicant view their own applications, decisions, payments, and profile
data — pulled from this system and from MCabinet. UI layout
preferences saved per-user.

## TOR / UC mapping

- **UC13** — Profil solicitant.
- TOR clauses: CF 13.*, MR 005.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/personal-account/overview` | `CnasUser` (citizen role) | Personal dashboard |
| `GET /api/personal-account/applications` | `CnasUser` | Own applications |
| `GET /api/personal-account/decisions` | `CnasUser` | Own decisions |
| `GET /api/personal-account/payments` | `CnasUser` | Own payment history |
| `GET /api/profile` | `CnasUser` | Own profile |
| `PATCH /api/profile` | `CnasUser` | Edit own profile |
| `GET /api/sessions` | `CnasUser` | Own sessions |
| `POST /api/user-layout-preferences` | `CnasUser` | Save UI layout |
| `GET /api/solicitants/{sqid}` | `CnasUser` (own row) | Solicitant detail |

## Code map

- Controllers
  - [`PersonalAccountController.cs`](../../src/Cnas.Ps.Api/Controllers/PersonalAccountController.cs)
  - [`ProfileController.cs`](../../src/Cnas.Ps.Api/Controllers/ProfileController.cs)
  - [`SessionsController.cs`](../../src/Cnas.Ps.Api/Controllers/SessionsController.cs)
  - [`UserLayoutPreferencesController.cs`](../../src/Cnas.Ps.Api/Controllers/UserLayoutPreferencesController.cs)
  - [`SolicitantsController.cs`](../../src/Cnas.Ps.Api/Controllers/SolicitantsController.cs)
- Application services
  - `IProfileService`, `ISolicitantService`.

## Data model

| Entity | Purpose |
|---|---|
| `Solicitant` | Applicant profile — encrypted PII (`NationalId`, `BankIban`, `PhoneE164`). |
| `UserProfile` | Cross-app profile row. Encrypted `NationalId`, `PhoneE164`. |
| `UserLayoutPreference` | Per-user UI layout serialisation. |

## Business rules

- Citizens see **only their own data** — enforced through
  `ICallerContext` + ABAC scope filtering at the service layer, not
  just the UI.
- Status snapshots in the citizen view come from MCabinet via
  `IMCabinetPublisher` (read-side) plus local DB join — MCabinet is
  the canonical citizen channel.
- Layout preferences are JSON blobs scoped to the user; non-sensitive,
  not audited beyond the standard `AuditableEntity` fields.

## Tests

- `tests/Cnas.Ps.Application.Tests/Profile/`
- `tests/Cnas.Ps.E2E.Tests/Journeys/PersonalAccountJourneyTests.cs`

## What's NOT here

- Citizen-facing in-app messaging — notifications and document
  download cover the surface today.
- Anonymous applicant intake — see [`public-portal.md`](public-portal.md)
  for the unauthenticated prefill flow.
