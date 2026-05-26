# Feature — Helpdesk & help content

## What it is

In-system support ticketing for end users and operators, plus the
editable help-topic catalogue surfaced on the public portal. Tickets
are categorised, routed via workflow, and audited.

## TOR / UC mapping

- TOR clauses: CF 01.*, MR (support), STAB.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/helpdesk/tickets/mine` | `CnasUser` | Own tickets |
| `POST /api/helpdesk/tickets` | `CnasUser` | Create a ticket |
| `POST /api/helpdesk/tickets/{sqid}/comments` | `CnasUser` | Comment |
| `POST /api/helpdesk/tickets/{sqid}/resolve` | `CnasDecider` | Resolve |
| `POST /api/helpdesk/ticket-categories` | `CnasAdmin` | Category admin |
| `GET /api/help/topics` | `CnasUser` | Authenticated help browse |
| `POST /api/help-admin/topics` | `CnasAdmin` | Edit help topic + translations |
| `POST /api/change-requests` | `CnasUser` | Request a change |

## Code map

- Controllers
  - [`Helpdesk/SupportTicketsController.cs`](../../src/Cnas.Ps.Api/Controllers/Helpdesk/SupportTicketsController.cs)
  - [`Helpdesk/SupportTicketCategoriesController.cs`](../../src/Cnas.Ps.Api/Controllers/Helpdesk/SupportTicketCategoriesController.cs)
  - [`HelpAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/HelpAdminController.cs)
  - [`HelpPublicController.cs`](../../src/Cnas.Ps.Api/Controllers/HelpPublicController.cs)
  - [`ChangeRequestsController.cs`](../../src/Cnas.Ps.Api/Controllers/ChangeRequestsController.cs)

## Data model

| Entity | Purpose |
|---|---|
| `SupportTicket` (referenced) | Ticket row + state + priority. |
| `SupportTicketCategory` | Per-category routing + SLA. |
| `HelpTopic` + `HelpTopicTranslation` | Help catalogue with per-locale content. |
| `ChangeRequest` | Examiner-initiated request for additional information. |

## Business rules

- Tickets route through `IWorkflowEngine` instances configured under
  category-specific workflow definitions.
- Public help is anonymous-readable (UC01); admin editing requires
  `CnasAdmin`.
- Categories carry the `NameRo` / `NameRu` / `NameEn` localisation
  trio per R0027.

## Tests

- `tests/Cnas.Ps.Application.Tests/Helpdesk/`

## What's NOT here

- External helpdesk integration (e.g. ServiceNow) — not in scope.
  Internal-only.
- Citizen-facing change-request workflow detail — see
  [`examination.md`](examination.md).
