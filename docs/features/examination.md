# Feature — Examinare (Document examination & verdicts)

## What it is

The "Examinare" leg of *Cerere → Examinare → Decizie → Plată*. Examiners
review documents attached to a dossier, record per-document verdicts
(Accept / Reject / Clarification needed), and progress workflow tasks
through approvals. Powers the day-to-day clerk inbox.

## TOR / UC mapping

- **UC04** — Dashboard (task inbox view).
- **UC05** — Execut sarcini (task execution).
- **UC08** — Examinare document (per-document verdict).
- **UC10** — Aprob / resping (department head approvals).
- TOR clauses: CF 04.*, CF 05.*, CF 08.*, CF 10.*, MR 006.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `GET /api/tasks/inbox` | `CnasUser` | `Authenticated` |
| `POST /api/tasks/{sqid}/claim` | `CnasUser` | `Authenticated` |
| `POST /api/tasks/{sqid}/complete` | `CnasUser` | `Authenticated` |
| `POST /api/examination/documents/{sqid}/verdict` | `CnasUser` | `Authenticated` |
| `POST /api/approvals/{sqid}/approve` | `CnasDecider` | `Authenticated` |
| `POST /api/approvals/{sqid}/reject` | `CnasDecider` | `Authenticated` |
| `POST /api/change-requests` | `CnasUser` | `Authenticated` |
| `GET /api/workflow-task-history/{sqid}` | `CnasUser` | `Authenticated` |

## Code map

- Controllers
  - [`ExaminationController.cs`](../../src/Cnas.Ps.Api/Controllers/ExaminationController.cs)
  - [`ApprovalsController.cs`](../../src/Cnas.Ps.Api/Controllers/ApprovalsController.cs)
  - [`TasksController.cs`](../../src/Cnas.Ps.Api/Controllers/TasksController.cs)
  - [`ChangeRequestsController.cs`](../../src/Cnas.Ps.Api/Controllers/ChangeRequestsController.cs)
  - [`WorkflowTaskHistoryController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowTaskHistoryController.cs)
- Application services
  - `IDocumentExaminationService`, `IApprovalWorkspaceService`,
    `ITaskInboxService`, `IExaminerAssignmentService`.

## Data model

| Entity | Purpose |
|---|---|
| `WorkflowTask` | A single unit of work assigned to a user / group. |
| `Document` | The artefact being examined; verdict appended via state. |
| `Dossier` | Container for one application's documents + tasks. |
| `ChangeRequest` | Examiner-initiated request for clarification from the citizen. |
| `ExaminerAssignmentCursor` | Round-robin / load-balancing cursor for assignment. |

## Workflows

```
Examiner browser
  ↓
POST /api/examination/documents/{sqid}/verdict + VerdictRequest
  ↓
ExaminationController.RecordVerdictAsync
  ├─ AuthZ: CnasUser
  ├─ RateLimit: Authenticated
  ├─ Sqid → long via ISqidService
  ↓
IDocumentExaminationService.RecordVerdictAsync
  ├─ Load Dossier + Document
  ├─ Apply transition rule from workflow definition
  ├─ AuditService.LogAsync
  ├─ Increment OTel counter examination.verdicts.recorded
  ↓
DB SaveChanges (transactional) + AuditLog row
```

The DossierSlaMonitorJob runs every 15 min and flags tasks whose
`DueAtUtc` has passed without completion, notifying the assignee
(see [`background-jobs.md`](background-jobs.md)).

## Business rules

- Verdicts are append-only on the `Document` state stream — corrections
  require a new verdict and a `ChangeRequest`.
- Claim-then-complete pattern — a task held by user A cannot be
  completed by user B unless A releases or a supervisor force-reassigns
  (audited as a sensitive action).
- Approvals (UC10) implement a 4-eyes check: the same user cannot
  both submit and approve. Enforced in `IApprovalWorkspaceService`,
  asserted by integration tests.
- Examination decisions feed `IDecisionWorkflowService` once all
  documents in the dossier are accepted.

## Tests

- `tests/Cnas.Ps.Application.Tests/Examination/`
- `tests/Cnas.Ps.Api.Tests/Controllers/ExaminationControllerTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/ExaminationJourneyTests.cs`

## What's NOT here

- Decision computation — see [`decisions.md`](decisions.md).
- Workflow definition admin — see [`workflows.md`](workflows.md).
