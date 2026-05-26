# Feature — Decizie (Decisions & recompute)

## What it is

The "Decizie" leg of *Cerere → Examinare → Decizie → Plată*. Computes
benefit amounts (pensions, allowances), issues formal Decisions,
supersedes prior Decisions when facts change, and feeds the payment
dispatcher. Includes the recompute engine that re-runs eligibility
when contribution adjustments or law changes arrive.

## TOR / UC mapping

- **UC11** — Descarc document (signed decision document).
- **UC21** — Procesare cerere (decision computation).
- Annex 3 — eligibility rules per life-event service.
- TOR clauses: CF 11.*, CF 21.*, MR 007, MR 009.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `POST /api/decisions` | `CnasDecider` | `Authenticated` |
| `GET /api/decisions/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/decisions/{sqid}/recompute` | `CnasDecider` | `Authenticated` |
| `POST /api/decisions/{sqid}/supersede` | `CnasDecider` | `Authenticated` |
| `POST /api/recovery-decisions` | `CnasDecider` | `Authenticated` |
| `POST /api/capitalised-payment-requests` | `CnasDecider` | `Authenticated` |
| `POST /api/executory-documents` | `CnasDecider` | `Authenticated` |
| `POST /api/mass-recalculation` | `CnasAdmin` | `Authenticated` |
| `GET /api/pension/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/insolvency` | `CnasDecider` | `Authenticated` |
| `POST /api/intl-agreement-review-cases` | `CnasDecider` | `Authenticated` |
| `POST /api/payment-corrections` | `CnasDecider` | `Authenticated` |

## Code map

- Controllers
  - [`DecisionsController.cs`](../../src/Cnas.Ps.Api/Controllers/DecisionsController.cs)
  - [`DecisionRecomputeController.cs`](../../src/Cnas.Ps.Api/Controllers/DecisionRecomputeController.cs)
  - [`DecisionSupersessionController.cs`](../../src/Cnas.Ps.Api/Controllers/DecisionSupersessionController.cs)
  - [`RecoveryDecisionsController.cs`](../../src/Cnas.Ps.Api/Controllers/RecoveryDecisionsController.cs)
  - [`CapitalisedPaymentRequestsController.cs`](../../src/Cnas.Ps.Api/Controllers/CapitalisedPaymentRequestsController.cs)
  - [`ExecutoryDocumentsController.cs`](../../src/Cnas.Ps.Api/Controllers/ExecutoryDocumentsController.cs)
  - [`MassRecalculationAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MassRecalculationAdminController.cs)
  - [`PensionController.cs`](../../src/Cnas.Ps.Api/Controllers/PensionController.cs)
  - [`InsolvencyController.cs`](../../src/Cnas.Ps.Api/Controllers/InsolvencyController.cs)
  - [`IntlAgreementReviewCasesController.cs`](../../src/Cnas.Ps.Api/Controllers/IntlAgreementReviewCasesController.cs)
  - [`PaymentCorrectionsController.cs`](../../src/Cnas.Ps.Api/Controllers/PaymentCorrectionsController.cs)
- Application services
  - `IDecisionWorkflowService`, `IDecisionRecomputeService`,
    `IFisaDeCalculRecalculator`, `IPriorDecisionTerminator`,
    `IRefusedPensionFallbackCascade`, `IRecoveryDecisionService`.

## Data model

| Entity | Purpose |
|---|---|
| `Decision` (entity registered in `CnasDbContext`) | The formal decision row. |
| `DecisionSupersession` | Edges between a superseded decision and its replacement. |
| `BenefitPayment` | Recurring or one-off payment instalments computed from the Decision. |
| `RecoveryDecision` | Recovery of overpaid amounts. |
| `CapitalisedPaymentDecision` / `CapitalisedPaymentRequest` | Lump-sum capitalisation. |
| `ExecutoryDocument` | Forced-collection rows for unpaid recoveries. |
| `MassRecalculationAdmin` runs | Audit of bulk recompute jobs. |

## Workflows

```
Examination complete → IDecisionWorkflowService.IssueAsync
  ├─ Build Fisa de Calcul (contribution + dependent inputs snapshot)
  ├─ Evaluate eligibility rules (JsonRulesDecisionEngine v1 today; v2 DSL pending)
  ├─ Compute amount + schedule
  ├─ Create Decision + BenefitPayment[] rows (transactional)
  ├─ AuditService.LogAsync
  ├─ DocumentGenerationService.RenderDecisionDocxAsync (Annex 7)
  ├─ MSign sign (gated on MEGA WSDL — current placeholder)
  ├─ NotificationService → MNotify + in-app + MCabinet
  └─ Result<DecisionOutput> with Sqid id
```

Recompute:

```
POST /api/decisions/{sqid}/recompute
  ↓
IDecisionRecomputeService
  ├─ Rebuild Fisa de Calcul with current facts
  ├─ Re-evaluate eligibility rules
  ├─ If amount changed → create supersession edge + new Decision
  └─ If no change → record audit row, no new Decision
```

## Business rules

- A Decision is **immutable**. Corrections create a new Decision and a
  `DecisionSupersession` edge — never edit the old row.
- Mass recalculation is admin-only, runs in the background-job lane
  (`Quartz`), and is gated by a 4-eyes maker-checker — see
  [`identity-access.md`](identity-access.md).
- The `Fisa de Calcul` is a **snapshot** of every input used at decision
  time. Pension rate changes after the fact don't change historical
  decisions — they propagate via recompute.
- Refused-pension cascade — `IRefusedPensionFallbackCascade` checks
  whether a refused application qualifies for *alocație socială* and
  routes the case automatically.

## Tests

- `tests/Cnas.Ps.Application.Tests/Decisions/`
- `tests/Cnas.Ps.Application.Tests/Calculations/`
- `tests/Cnas.Ps.E2E.Tests/Journeys/DecisionJourneyTests.cs`

## What's NOT here

- Decision-engine DSL **v2** (date-older-than-N-days + nested OR
  composition) — documented in `TODO.md` §17 as a coherent v2; do
  **not** extend the v1 `JsonRulesDecisionEngine` ad-hoc.
- Payment dispatch to MPay — see [`payments.md`](payments.md).
- The MSign two-phase signing — gated on the MEGA WSDL; see
  [`mgov-integration.md`](mgov-integration.md).
