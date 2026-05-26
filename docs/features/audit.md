# Feature — Audit

## What it is

Append-only audit trail covering every privileged action, every access
to a sensitive field, every authentication and authorisation event.
Backed by Postgres for primary storage, mirrored to MLog for the
government-side trail. Audit categories and field policies are
admin-configurable. Integrity verifiable through `IntegrityCheckRun`.

## TOR / UC mapping

- **UC23** — Jurnalizez.
- TOR clauses: CF 23.*, SEC 038–043, SEC 056.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/audit-explorer` | `CnasAdmin` | Search the audit trail |
| `POST /api/audit-categories` | `CnasAdmin` | Manage audit categories |
| `POST /api/audit-field-policies` | `CnasAdmin` | Configure per-field policies |
| `POST /api/audit-policies` | `CnasAdmin` | Configure higher-level policies |
| `GET /api/admin-history` | `CnasAdmin` | Admin-action history |
| `POST /api/integrity-check-admin/run` | `CnasAdmin` | Trigger an integrity check |
| `POST /api/m-log-categories-admin` | `CnasAdmin` | MLog category mapping |
| `GET /api/legal-change-events` | `CnasAdmin` | Legal-change event log |

## Code map

- Controllers
  - [`AuditExplorerController.cs`](../../src/Cnas.Ps.Api/Controllers/AuditExplorerController.cs)
  - [`AuditCategoriesController.cs`](../../src/Cnas.Ps.Api/Controllers/AuditCategoriesController.cs)
  - [`AuditFieldPoliciesController.cs`](../../src/Cnas.Ps.Api/Controllers/AuditFieldPoliciesController.cs)
  - [`AuditPoliciesController.cs`](../../src/Cnas.Ps.Api/Controllers/AuditPoliciesController.cs)
  - [`AdminHistoryController.cs`](../../src/Cnas.Ps.Api/Controllers/AdminHistoryController.cs)
  - [`IntegrityCheckAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/IntegrityCheckAdminController.cs)
  - [`MLogCategoriesAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MLogCategoriesAdminController.cs)
  - [`LegalChangeEventsController.cs`](../../src/Cnas.Ps.Api/Controllers/LegalChangeEventsController.cs)
- Application services
  - `IAuditService`, `IMLogCategoryConfigService`.

## Data model

| Entity | Purpose |
|---|---|
| `AuditLog` | Append-only row: who, action, target, when, IP, correlation id, payload diff. |
| `AuditableEntity` (base) | Adds `CreatedAtUtc` / `UpdatedAtUtc` / actor to entities. |
| `EntityHistoryRow` | Per-row historical snapshot for selected entities. |
| `AuditCategory` + `AuditPolicy` + `AuditFieldPolicy` | Configuration of what gets logged and at what sensitivity. |
| `IntegrityCheckRun` + `IntegrityCheckFinding` | Periodic data-integrity checks. |
| `LegalChangeEvent` | Records when legal rate tables / coefficients changed. |

## Business rules

- `AuditLog` is **append-only**. There is no UPDATE or DELETE path.
- Every audit row carries `correlation_id` — the same id appears in
  Serilog logs and OTel traces so an audit row can be cross-linked to
  the request that produced it.
- MLog mirror is best-effort: the user-facing action succeeds even if
  MLog fan-out fails; the failure persists as a `FailedJob` for replay.
- Encrypted-field access is audited via `AuditFieldPolicy` — every
  decrypt-and-display logs the actor and the row id but not the
  plaintext.

## Tests

- `tests/Cnas.Ps.Application.Tests/Audit/`
- `tests/Cnas.Ps.Infrastructure.Tests/Persistence/AuditLogTests.cs`

## What's NOT here

- A SIEM connector — MLog is the canonical mirror. Production SIEM
  feeds off MLog, not directly off the AuditLog table.
