# Feature — Cerere (Application intake)

## What it is

The "Cerere" leg of the *Cerere → Examinare → Decizie → Plată* lifecycle.
Handles citizen-submitted applications for all 81 life-event services
defined in Annex 3 (pensions, allowances, disability, death,
adjacent benefits). Includes the Solicitant (applicant) profile,
attachments with magic-byte validation, version history, and the
intake-form pipeline.

## TOR / UC mapping

- **UC06** — Depunere cerere (submit application).
- **UC07** — Înregistrare formular (form intake).
- Annex 3 — 81 life-event services.
- TOR clauses: CF 06.*, CF 07.*, MR 005.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `POST /api/applications` | `CnasUser` | `Authenticated` |
| `GET /api/applications/{sqid}` | `CnasUser` | `Authenticated` |
| `GET /api/applications/{sqid}/versions` | `CnasUser` | `Authenticated` |
| `POST /api/applications/{sqid}/attachments` | `CnasUser` | `Upload` |
| `POST /api/applications/{sqid}/processing/start` | `CnasDecider` | `Authenticated` |
| `GET /api/solicitants/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/forms/intake` | `CnasUser` | `Authenticated` |
| `GET /api/fisa-de-calcul/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/paper-fulfilment` | `CnasDecider` | `Authenticated` |

## Code map

- Controllers
  - [`ApplicationsController.cs`](../../src/Cnas.Ps.Api/Controllers/ApplicationsController.cs)
  - [`ApplicationAttachmentsController.cs`](../../src/Cnas.Ps.Api/Controllers/ApplicationAttachmentsController.cs)
  - [`ApplicationProcessingController.cs`](../../src/Cnas.Ps.Api/Controllers/ApplicationProcessingController.cs)
  - [`ApplicationVersionsController.cs`](../../src/Cnas.Ps.Api/Controllers/ApplicationVersionsController.cs)
  - [`SolicitantsController.cs`](../../src/Cnas.Ps.Api/Controllers/SolicitantsController.cs)
  - [`FormsController.cs`](../../src/Cnas.Ps.Api/Controllers/FormsController.cs)
  - [`FisaDeCalculController.cs`](../../src/Cnas.Ps.Api/Controllers/FisaDeCalculController.cs)
  - [`PaperFulfilmentController.cs`](../../src/Cnas.Ps.Api/Controllers/PaperFulfilmentController.cs)
  - [`AttachmentsController.cs`](../../src/Cnas.Ps.Api/Controllers/AttachmentsController.cs)
- Application services
  - `IApplicationService`, `IApplicationProcessingService`,
    `IApplicationStatusGuard`, `IApplicationVersionService`.
  - `ISolicitantService`, `IFormIntakeService`,
    `IFisaDeCalculRecalculator`.

## Data model

| Entity | Purpose |
|---|---|
| `Application` | Top-level application row. Tied to a `ServicePassport`. |
| `ApplicationVersion` | Immutable per-edit snapshot. |
| `ApplicationAttachment` + `AttachmentRecord` | Files uploaded to MinIO, magic-byte validated. |
| `Solicitant` | Applicant profile (subset of UserProfile + bank IBAN + phone). PII encrypted. |
| `Dossier` | Per-application case folder; ties to workflow tasks. |
| `Document` | Generated documents (Annex-7 templates rendered). |

## Workflows

```
Citizen WASM
  ↓
POST /api/applications  +  SubmitApplicationInput (Sqid IDs)
  ↓
ApplicationsController.SubmitAsync
  ↓
IApplicationService.SubmitAsync
  ├─ FluentValidation
  ├─ Resolve ServicePassport → workflow definition
  ├─ Create Application + Dossier + initial WorkflowTask
  ├─ AuditService.LogAsync (immutable snapshot)
  ├─ NotificationService.EnqueueAsync (in-app + MNotify)
  └─ MCabinetPublisher.PublishAsync (citizen e-cabinet status)
  ↓
201 Created + ApplicationOutput (Sqid IDs only)
```

## Business rules

- Every uploaded attachment must pass magic-byte validation
  (`IFileStorage.PutAsync` enforces it — SEC 010). Extension-only
  validation is explicitly forbidden (red-flag #10 in CLAUDE.md).
- Per-application Sqid ID is generated server-side; clients cannot
  influence it.
- Editing an application after submission creates an
  `ApplicationVersion` row and increments the version counter — the
  prior version is preserved in full.
- `IApplicationStatusGuard` blocks state transitions that the active
  workflow would not allow, even if a client tries to drive them
  directly.

## Tests

- `tests/Cnas.Ps.Application.Tests/Applications/`
- `tests/Cnas.Ps.Api.Tests/Controllers/ApplicationsControllerTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/ApplicationJourneyTests.cs`

## What's NOT here

- Decision computation — see [`decisions.md`](decisions.md).
- Workflow task assignment + SLA monitoring — see
  [`workflows.md`](workflows.md).
- Payment dispatch — see [`payments.md`](payments.md).
