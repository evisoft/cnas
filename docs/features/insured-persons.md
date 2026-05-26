# Feature — Persoane asigurate (Insured persons)

## What it is

Registry of insured persons keyed by IDNP. Holds personal identification
(encrypted at rest, hash-shadow for lookups), contribution history,
labor-booklet data, pre-1999 work periods, athlete-career records, and
the per-person aggregated contribution adjustments used by decision
calculations.

## TOR / UC mapping

- **UC03** — Caut / vizualizez (search + view).
- **UC12** — Explorez registru.
- Annex 2 — Persoane asigurate.
- TOR clauses: CF 03.*, CF 12.*, MR 002.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `GET /api/insured-persons` | `CnasUser` | `Authenticated` |
| `GET /api/insured-persons/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/insured-persons` | `CnasDecider` | `Authenticated` |
| `PATCH /api/insured-persons/{sqid}` | `CnasDecider` | `Authenticated` |
| `POST /api/insured-persons/{sqid}/adjustments` | `CnasDecider` | `Authenticated` |
| `GET /api/labor-booklets/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/labor-booklets` | `CnasDecider` | `Authenticated` |
| `GET /api/pre1999-stagiu/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/athlete-pensions/awards` | `CnasDecider` | `Authenticated` |

## Code map

- Controllers
  - [`InsuredPersonsController.cs`](../../src/Cnas.Ps.Api/Controllers/InsuredPersonsController.cs)
  - [`InsuredPersonAdjustmentsController.cs`](../../src/Cnas.Ps.Api/Controllers/InsuredPersonAdjustmentsController.cs)
  - [`LaborBookletsController.cs`](../../src/Cnas.Ps.Api/Controllers/LaborBookletsController.cs)
  - [`Pre1999StagiuController.cs`](../../src/Cnas.Ps.Api/Controllers/Pre1999StagiuController.cs)
  - [`AthletePensionAwardsController.cs`](../../src/Cnas.Ps.Api/Controllers/AthletePensionAwardsController.cs)
- Application services — `IInsuredPersonService`, `IDataSearchService`.

## Data model

| Entity | Purpose |
|---|---|
| `InsuredPerson` | Core row. `Idnp` encrypted, `IdnpHash` unique-indexed shadow. |
| `InsuredPersonContributionAdjustment` | Manual + automatic adjustments. Append-only. |
| `InsuredPersonPre1999Period` | Pre-1999 contribution period rows. |
| `LaborBooklet` | Carnet de muncă records (paper-era work history). |
| `AthleteCareerRecord` | High-performance athlete career rows feeding athlete pensions. |
| `AthletePensionAward` | Awards specific to the athlete-pension category. |

## Business rules

- IDNP equality lookup is index-backed via `IdnpHash`. Range queries
  on IDNP are not supported (the hash is opaque).
- `InsuredPersonContributionAdjustment` is **append-only** — a
  correction creates a new row that supersedes the previous one. The
  audit chain is built into the row itself, not into `AuditLog`.
- Refresh from RSP via `MConnectSyncJob` daily — see
  [`background-jobs.md`](background-jobs.md). The job updates only
  fields the registry owns (name, civil status); contribution data
  comes from declarations.
- Pre-1999 stagiu rows feed `IFisaDeCalculRecalculator` when the
  decision engine builds the contribution profile.

## Tests

- `tests/Cnas.Ps.Application.Tests/InsuredPersons/`
- `tests/Cnas.Ps.Infrastructure.Tests/Persistence/InsuredPersonRepositoryTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/InsuredPersonJourneyTests.cs`

## What's NOT here

- The 4 Annex-2 BPMN business processes are realised in the workflow
  configurations, not in dedicated controllers.
- Citizen-facing read of their own insured-person record is
  [`personal-account.md`](personal-account.md), not here.
