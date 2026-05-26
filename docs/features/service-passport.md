# Feature — Service passport & catalog

## What it is

The configuration source-of-truth for every life-event service the
system offers. A `ServicePassport` row binds together: human-readable
multi-locale description, eligibility rules, workflow definition,
document-template set, deadline configuration, and notification
strategy. Plus the classifier catalogue that every dropdown in the UI
reads from.

## TOR / UC mapping

- **UC15** — Configurez serviciu.
- Annex 3 — 81 life-event services (each one is a ServicePassport).
- TOR clauses: CF 15.*, CF 17.13.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/service-passports` | `CnasUser` | List service passports |
| `GET /api/service-passports/{sqid}` | `CnasUser` | Read one |
| `POST /api/service-passports` | `CnasAdmin` | Create / edit |
| `POST /api/service-passports/{sqid}/rules` | `CnasAdmin` | Edit eligibility rules |
| `POST /api/service-passports/{sqid}/config-matrix` | `CnasAdmin` | Edit per-passport config matrix |
| `POST /api/service-catalog-config` | `CnasAdmin` | Global catalog config |
| `GET /api/classification-catalog-admin` | `CnasAdmin` | Classifier admin |
| `POST /api/voucher-quotas` | `CnasAdmin` | Voucher quota config |
| `GET /api/registers` | `CnasUser` | Cross-registry index |

## Code map

- Controllers
  - [`ServicePassportsController.cs`](../../src/Cnas.Ps.Api/Controllers/ServicePassportsController.cs)
  - [`ServiceCatalogConfigController.cs`](../../src/Cnas.Ps.Api/Controllers/ServiceCatalogConfigController.cs)
  - [`ClassificationCatalogAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/ClassificationCatalogAdminController.cs)
  - [`VoucherQuotasController.cs`](../../src/Cnas.Ps.Api/Controllers/VoucherQuotasController.cs)
  - [`RegistersController.cs`](../../src/Cnas.Ps.Api/Controllers/RegistersController.cs)
- Application services
  - `IServicePassportService`, `IServicePassportConfigMatrixService`,
    `IServicePassportRulesEditorService`, `IServiceCatalogConfigService`,
    `IClassifierService`, `IVoucherQuotaService`.

## Data model

| Entity | Purpose |
|---|---|
| `ServicePassport` | Per-life-event service definition. `NameRo` / `NameRu` / `NameEn`. |
| `Classifier` | Lookup row (CSM categories, civil status, etc.). `LabelRo/Ru/En`. |
| `ClassificationCatalogEntry` + `ClassificationCatalogSnapshot` | Versioned classifier publishes; drift findings via `ClassificationDriftFinding`. |
| `VoucherQuota` | Configured per-service quotas. |
| `DocumentTemplate` | Linked template set per service (Annex 7). |

## Business rules

- ServicePassport changes are versioned and pinned — an Application
  bound to passport v3 stays on v3 across passport edits.
- Classifier publishes are snapshot-based: drift detection compares
  the in-flight snapshot against the active one and flags
  `ClassificationDriftFinding` rows for review.
- 80 service-passport seeds ship in `src/Cnas.Ps.Infrastructure/Persistence/Seed/`.

## Tests

- `tests/Cnas.Ps.Application.Tests/ServicePassports/`
- `tests/Cnas.Ps.Infrastructure.Tests/Seed/ServicePassportSeedTests.cs`

## What's NOT here

- UC16 workflow-graph admin — see [`workflows.md`](workflows.md).
- UC17 template admin — see [`document-templates.md`](document-templates.md).
