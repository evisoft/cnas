# Feature — Plătitori de contribuții (Contributors)

## What it is

Registry of contribution payers (employers, self-employed, individual
contractors). Holds identification (IDNO, hash-shadowed for indexed
equality lookup), legal form, registered address, activity periods,
linked entities, and history of profile refreshes from external
registries.

## TOR / UC mapping

- **UC03** — Caut / vizualizez registru (search + view).
- **UC12** — Explorez registru (deep browse).
- Annex 1 — Plătitori de contribuții (13 BPs).
- TOR clauses: CF 03.*, CF 12.*, MR 003.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `GET /api/contributors` | `CnasUser` | `Authenticated` |
| `GET /api/contributors/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/contributors` | `CnasDecider` | `Authenticated` |
| `PATCH /api/contributors/{sqid}` | `CnasDecider` | `Authenticated` |
| `GET /api/contributors/{sqid}/linked-entities` | `CnasUser` | `Authenticated` |
| `POST /api/contributors/{sqid}/profile-refresh` | `CnasDecider` | `Authenticated` |
| `GET /api/contributors/{sqid}/source-history` | `CnasUser` | `Authenticated` |
| `GET /api/contributors/{sqid}/profile-updates` | `CnasUser` | `Authenticated` |
| `GET /api/payers/{sqid}/linked-entities` | `CnasUser` | `Authenticated` |

## Code map

- Controllers
  - [`ContributorsController.cs`](../../src/Cnas.Ps.Api/Controllers/ContributorsController.cs)
  - [`ContributorLinkedEntitiesController.cs`](../../src/Cnas.Ps.Api/Controllers/ContributorLinkedEntitiesController.cs)
  - [`ContributorProfileRefreshController.cs`](../../src/Cnas.Ps.Api/Controllers/ContributorProfileRefreshController.cs)
  - [`ContributorProfileUpdatesController.cs`](../../src/Cnas.Ps.Api/Controllers/ContributorProfileUpdatesController.cs)
  - [`ContributorSourceHistoryController.cs`](../../src/Cnas.Ps.Api/Controllers/ContributorSourceHistoryController.cs)
  - [`PayerLinkedEntitiesController.cs`](../../src/Cnas.Ps.Api/Controllers/PayerLinkedEntitiesController.cs)
- Application services — `IContributorService`, `IDataSearchService`.
- Infrastructure
  - `ContributorService` — write path on `ICnasDbContext`; listing
    branches deferred to replica via `IReadOnlyCnasDbContext`.
  - External registry adapters (`RspClient`, `RsudClient`) for
    profile-refresh.

## Data model

| Entity | Purpose |
|---|---|
| `Contributor` | Core registry row. `Idno` encrypted AES-256-GCM, `IdnoHash` (HMAC-SHA256) shadow column with unique index. |
| `ContributorAddress` | One-to-many addresses (registered, mailing). |
| `ContributorContact` | One-to-many contact rows (phone, email). |
| `ContributorCivilStatus` | Civil-status history for individual payers. |
| `ContributorActivityPeriod` | Active-period intervals (start/end UTC). |
| `ContributorPeriodProjection` | Materialised view-ish row used by reporting + recomputation. |
| `ContributorSourceChangeHistory` | Audit of profile-refresh deltas from external registries. |
| `ContributorPre1999PeriodCarnetMunca` | Pre-1999 work-record book entries. |
| `ContributorSocialInsuranceContract` | Voluntary social-insurance contracts. |

Equality search by IDNO is index-backed on `IdnoHash`. Plain
`Idno` is never queryable — only the encrypted column round-trips
through `AesFieldEncryptor` for display.

## Workflows

```
Profile refresh:
  POST /api/contributors/{sqid}/profile-refresh
    ↓
  ContributorProfileRefreshService
    ↓
  RspClient / RsudClient (MConnect SOAP, gated)
    ↓
  Diff vs current row → ContributorSourceChangeHistory + AuditLog
    ↓
  Update Contributor + dependent rows (transactional)
```

## Business rules

- IDNO uniqueness is enforced at the hash-shadow column level —
  duplicate IDNOs cannot be inserted even if the plaintext encrypts to
  different ciphertext (AES-GCM is non-deterministic).
- Soft delete only (`IsActive=false`); hard delete reserved for GDPR
  removals via the 4-eyes admin path.
- Address + contact changes append, never overwrite — the registry is
  an immutable history of changes (R0027 + CLAUDE.md "Immutable
  snapshots").

## Tests

- `tests/Cnas.Ps.Application.Tests/Contributors/`
- `tests/Cnas.Ps.Infrastructure.Tests/Persistence/ContributorRepositoryTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/ContributorJourneyTests.cs`

## What's NOT here

- The 13 Annex-1 BPMN business processes are not modelled as separate
  workflow definitions — they are realised by individual endpoints
  triggering `IWorkflowEngine` steps.
- Insolvency processing lives in [`decisions.md`](decisions.md) via
  `InsolvencyController`.
