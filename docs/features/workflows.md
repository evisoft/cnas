# Feature — Workflow engine

## What it is

Workflow orchestration for the *Cerere → Examinare → Decizie → Plată*
lifecycle and any other multi-step process. Defines workflow graphs,
assigns tasks, enforces transitions, tracks SLA, escalates on
overdue. Backed by Operaton (Camunda 7-compatible) — adapter today is
shape-only, the full Operaton epic is externally gated on server
provisioning.

## TOR / UC mapping

- **UC04** — Dashboard (task inbox).
- **UC05** — Execut sarcini.
- **UC16** — Configurez flux (workflow admin).
- **UC20** — Proceduri automate (automation triggers).
- TOR clauses: CF 04.*, CF 05.*, CF 16.*, CF 20.*.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/workflows` | `CnasUser` | List workflow definitions |
| `POST /api/workflow-graphs-admin` | `CnasAdmin` | Create / edit a workflow graph |
| `POST /api/workflows/{sqid}/start` | `CnasUser` | Start a new instance |
| `POST /api/workflow-step-acls` | `CnasAdmin` | Configure per-step ACL |
| `POST /api/workflow-notification-strategies` | `CnasAdmin` | Per-step notification strategy |
| `POST /api/automation` | `CnasAdmin` | Trigger automation runs |
| `POST /api/automation-schedules` | `CnasAdmin` | Schedule recurring automation |
| `GET /api/business-hours-policies` | `CnasUser` | Read business-hours config (SLA calendar) |
| `POST /api/peak-hour-gate-admin` | `CnasTechAdmin` | Toggle peak-hour gating |

## Code map

- Controllers
  - [`WorkflowsController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowsController.cs)
  - [`WorkflowGraphsAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowGraphsAdminController.cs)
  - [`WorkflowStepAclsController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowStepAclsController.cs)
  - [`WorkflowNotificationStrategiesController.cs`](../../src/Cnas.Ps.Api/Controllers/WorkflowNotificationStrategiesController.cs)
  - [`AutomationController.cs`](../../src/Cnas.Ps.Api/Controllers/AutomationController.cs)
  - [`AutomationSchedulesController.cs`](../../src/Cnas.Ps.Api/Controllers/AutomationSchedulesController.cs)
  - [`BusinessHoursPoliciesController.cs`](../../src/Cnas.Ps.Api/Controllers/BusinessHoursPoliciesController.cs)
  - [`PeakHourGateAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/PeakHourGateAdminController.cs)
- Application services
  - `IWorkflowEngine`, `IWorkflowConfigurationService`,
    `IAutomationService`, `IExaminerAssignmentService`,
    `ITaskInboxService`.
- Infrastructure
  - `OperatonWorkflowEngine.cs` — shape-only adapter pending Operaton
    server provisioning (externally gated).

## Data model

| Entity | Purpose |
|---|---|
| `WorkflowDefinition` | Versioned definition (identified by `Code`). |
| `WorkflowGraph` / `WorkflowGraphNode` | Visual editor representation. |
| `WorkflowTask` | A single task instance with assignee + due date. |
| `WorkflowStepAcl` | Per-step ACL — who can claim / complete. |
| `BusinessHoursPolicy` | SLA calendar (e.g. Mo–Fr 9–18 Chișinău time). |
| `JobScheduleOverride` | Per-schedule overrides used by automation. |

## Workflows

```
ServicePassport.WorkflowCode → WorkflowDefinition (pinned version)
                                  ↓
                          Workflow instance per Application
                                  ↓
                          WorkflowTask[] created per step
                                  ↓
                          Assigned via IExaminerAssignmentService
                                  ↓
                          DossierSlaMonitorJob flags overdue tasks
```

## Business rules

- Workflow versions are **pinned**: an Application started on
  workflow v3 stays on v3 even if v4 is published.
- SLA timers respect `BusinessHoursPolicy` — weekends and public
  holidays don't count toward elapsed time.
- The peak-hour gate (`PeakHourGateAdminController`) can throttle
  non-essential automation during business-critical windows
  (declaration submission deadline days).
- Examiner assignment is load-balancing by default; supervisors can
  override via the inbox UI (audited as sensitive action).

## Tests

- `tests/Cnas.Ps.Application.Tests/Workflow/`
- `tests/Cnas.Ps.Infrastructure.Tests/Workflow/`

## What's NOT here

- Full Operaton integration is externally gated; today the adapter
  produces deterministic results so the rest of the system stays
  testable.
- BPM modelling tool — graphs are defined via the admin endpoints +
  Blazor UI; there is no Camunda Modeler ingestion.
