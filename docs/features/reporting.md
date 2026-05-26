# Feature — Reporting, dashboards & search

## What it is

Annex-6 statistical reports (~50 typed queries across Annex 6 / 6b … 6j),
ad-hoc query builder, dashboards (operator + admin + KPI), and the
global full-text search. All read traffic for reporting routes through
the `IReadOnlyCnasDbContext` replica (R0026 / PSR 006). Long-running
queries off the request lane via `[LongRunningReportService]`.

## TOR / UC mapping

- **UC03** — Caut / vizualizez (search).
- **UC09** — Extrag rapoarte.
- **UC12** — Explorez registru.
- **UC19** — Generez rapoarte.
- TOR clauses: CF 09.*, CF 12.*, CF 19.*, PSR 006, ARH 025.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/reports` | `CnasUser` | List standard reports |
| `POST /api/reports/{sqid}/run` | `CnasUser` | Execute report (or queue) |
| `GET /api/report-jobs/{sqid}` | `CnasUser` | Poll long-running report status |
| `POST /api/ad-hoc-reports` | `CnasDecider` | Ad-hoc report submission |
| `POST /api/report-catalog-admin` | `CnasAdmin` | Catalog admin |
| `POST /api/report-distribution-rules` | `CnasAdmin` | Per-report distribution |
| `GET /api/dashboard` | `CnasUser` | Operator dashboard tiles |
| `GET /api/admin-dashboard` | `CnasAdmin` | Admin dashboard |
| `GET /api/kpi-dashboard` | `CnasAdmin` | KPI tiles |
| `GET /api/search?q=` | `CnasUser` | Global full-text search |
| `GET /api/global-search` | `CnasUser` | Same, structured input |
| `POST /api/saved-searches` | `CnasUser` | Save a query |
| `POST /api/grid-exports` | `CnasUser` | Grid → CSV / XLSX export |
| `GET /api/archive-summary` | `CnasUser` | Archive overview |
| `GET /api/access-rights-reports` | `CnasAdmin` | Access / permission reports |
| `POST /api/etl-projections` | `CnasAdmin` | ETL projection management |

## Code map

- Controllers
  - [`ReportsController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportsController.cs)
  - [`ReportJobsController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportJobsController.cs)
  - [`AdHocReportsController.cs`](../../src/Cnas.Ps.Api/Controllers/AdHocReportsController.cs)
  - [`ReportCatalogAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportCatalogAdminController.cs)
  - [`ReportDistributionRulesController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportDistributionRulesController.cs)
  - [`ReportingAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportingAdminController.cs)
  - [`DashboardController.cs`](../../src/Cnas.Ps.Api/Controllers/DashboardController.cs)
  - [`AdminDashboardController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminDashboardController.cs)
  - [`KpiDashboardController.cs`](../../src/Cnas.Ps.Api/Controllers/KpiDashboardController.cs)
  - [`GlobalSearchController.cs`](../../src/Cnas.Ps.Api/Controllers/GlobalSearchController.cs)
  - [`SavedSearchesController.cs`](../../src/Cnas.Ps.Api/Controllers/SavedSearchesController.cs)
  - [`GridExportsController.cs`](../../src/Cnas.Ps.Api/Controllers/GridExportsController.cs)
  - [`ArchiveSummaryController.cs`](../../src/Cnas.Ps.Api/Controllers/ArchiveSummaryController.cs)
  - [`AccessRightsReportsController.cs`](../../src/Cnas.Ps.Api/Controllers/AccessRightsReportsController.cs)
  - [`EtlProjectionsController.cs`](../../src/Cnas.Ps.Api/Controllers/EtlProjectionsController.cs)
  - [`RegistryExportProjection.cs`](../../src/Cnas.Ps.Api/Controllers/RegistryExportProjection.cs)
- Application services
  - `IReportingService` — Annex 6/6b/…/6j partials.
  - `IDataSearchService` — UC03 / UC12 registry search.
  - `IDashboardService` — tile producers.
  - `ISavedSearchService`.
- Infrastructure
  - `PostgresGlobalSearchService` — Postgres FTS fallback
    (`plainto_tsquery('romanian', …)` + GIN indexes).
  - `ReportingService` + 50 partials.

## Business rules

- All read paths in this feature consume `IReadOnlyCnasDbContext`.
- Long-running queries (>2 s expected) carry the
  `[LongRunningReportService]` marker so the request lane never
  starves on them.
- Grid exports run server-side and stream — never load the whole
  result set into memory.
- Saved searches store the **query**, not the **result** — re-running
  picks up new rows.

## Tests

- `tests/Cnas.Ps.Infrastructure.Tests/Reporting/`
- `tests/Cnas.Ps.Infrastructure.Tests/Search/PostgresGlobalSearchServiceTests.cs`
- `tests/Cnas.Ps.Application.Tests/Validators/GlobalSearchInputValidatorTests.cs`

## What's NOT here

- Embedded BI engine (Stimulsoft / Jasper / Pentaho) — deferred to UAT
  outcome. The in-process LINQ/EF builder is today's strategy; the
  `IReportEngine` + `IReportExporter` seams make the swap a single
  strategy substitution.
- Elasticsearch / OpenSearch — Postgres FTS is the current fallback;
  `IFullTextSearchEngine` (R0522) is the future swap seam.
