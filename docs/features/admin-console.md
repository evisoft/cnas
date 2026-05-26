# Feature — Admin console

## What it is

Catch-all administrative surface that doesn't belong inside a single
business module: cross-module bulk actions, generic admin dashboard,
permission editing, history browse, sensitive-action submission,
management-period close, and the failed-job replay queue. Most
endpoints here require `CnasAdmin` and the destructive ones route
through the 4-eyes maker-checker queue.

## TOR / UC mapping

- Cross-cutting between UC15, UC16, UC17, UC18, UC20.
- TOR clauses: CF 18.*, CF 20.*.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/admin` | `CnasAdmin` | Admin landing data |
| `GET /api/admin-dashboard` | `CnasAdmin` | Dashboard tiles |
| `GET /api/admin-history` | `CnasAdmin` | Admin-action history |
| `POST /api/admin-permissions` | `CnasAdmin` | Permission edits |
| `POST /api/bulk-actions/run` | `CnasAdmin` | Cross-entity bulk action |
| `POST /api/sensitive-admin-actions` | `CnasAdmin` | Submit a sensitive action |
| `POST /api/pending-admin-actions/{sqid}/approve` | `CnasAdmin` (checker) | Approve 4-eyes |
| `POST /api/pending-admin-actions/{sqid}/reject` | `CnasAdmin` (checker) | Reject 4-eyes |
| `POST /api/management-period/close` | `CnasAdmin` | Close a management period |
| `GET /api/quality-risks` | `CnasAdmin` | Quality-risk register |
| `POST /api/database/health` | `CnasTechAdmin` | Database health admin |
| `POST /api/admin/failed-jobs/{id}/replay` | `CnasAdmin` | Replay a failed job |

## Code map

- Controllers
  - [`AdminController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminController.cs)
  - [`AdminDashboardController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminDashboardController.cs)
  - [`AdminHistoryController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminHistoryController.cs)
  - [`AdminPermissionsController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminPermissionsController.cs)
  - [`BulkActionsController.cs`](../../src/Cnas.Ps.Api/Controllers/BulkActionsController.cs)
  - [`PendingAdminActionsController.cs`](../../src/Cnas.Ps.Api/Controllers/PendingAdminActionsController.cs)
  - [`SensitiveAdminActionsController.cs`](../../src/Cnas.Ps.Api/Controllers/SensitiveAdminActionsController.cs)
  - [`ManagementPeriodController.cs`](../../src/Cnas.Ps.Api/Controllers/ManagementPeriodController.cs)
  - [`QualityRisksController.cs`](../../src/Cnas.Ps.Api/Controllers/QualityRisksController.cs)
  - [`HealthDatabaseController.cs`](../../src/Cnas.Ps.Api/Controllers/HealthDatabaseController.cs)
- Application services
  - `IPendingAdminActionService`, `IPendingAdminActionExecutor`,
    `IUserAdministrationService`.

## Data model

| Entity | Purpose |
|---|---|
| `PendingAdminAction` | Maker row — payload + executor code + state. |
| `ManagementPeriodClose` | Per-period close gate. |
| `BulkOperationRun` + `BulkSelection` | Bulk-action audit. |
| `FailedJob` | DLQ entries. |

## Business rules

- Destructive admin actions go through 4-eyes — `PendingAdminAction`
  rows pair a maker with a checker; the same user cannot be both. The
  `MakerCheckerExpirySweeper` flips Pending → Expired on stale rows.
- Today only `NoOpDemoExecutor` (`DEMO.NOOP`) is wired as an
  executor; the first real destructive action routed through this
  queue is the deferred R0058-retrofit.
- Failed-job replay is admin-only and audited; consult
  [`background-jobs.md`](background-jobs.md) for the failure modes.

## Tests

- `tests/Cnas.Ps.Application.Tests/SensitiveActions/`
- `tests/Cnas.Ps.Api.Tests/Controllers/AdminControllerTests.cs`

## What's NOT here

- User CRUD lives in [`identity-access.md`](identity-access.md).
- Audit explorer lives in [`audit.md`](audit.md).
