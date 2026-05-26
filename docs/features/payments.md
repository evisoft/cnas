# Feature — Plăți prestații (Benefit payments)

## What it is

The "Plată" leg of *Cerere → Examinare → Decizie → Plată*. Generates
benefit payment schedules from Decisions, dispatches outbound payments
through MPay, handles the inbound MPay confirmation callback, processes
treasury feeds, and manages suspensions / corrections / recoveries.

## TOR / UC mapping

- **UC21** — Procesare cerere (payment side).
- TOR clauses: CF 21.*, MR 011, MR 012.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `GET /api/benefit-payments/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/payment-suspensions` | `CnasDecider` | `Authenticated` |
| `POST /api/payment-corrections` | `CnasDecider` | `Authenticated` |
| `GET /api/recurrent-payments` | `CnasUser` | `Authenticated` |
| `POST /api/treasury-payments` | `CnasDecider` | `Authenticated` |
| `GET /api/treasury-information` | `CnasUser` | `Authenticated` |
| `POST /api/treasury-feed-admin/ingest` | `CnasAdmin` | `Authenticated` |
| `POST /api/mpay/confirm` | none (mTLS / signed) | `Callback` |

## Code map

- Controllers
  - [`BenefitPaymentsController.cs`](../../src/Cnas.Ps.Api/Controllers/BenefitPaymentsController.cs)
  - [`PaymentSuspensionsController.cs`](../../src/Cnas.Ps.Api/Controllers/PaymentSuspensionsController.cs)
  - [`PaymentCorrectionsController.cs`](../../src/Cnas.Ps.Api/Controllers/PaymentCorrectionsController.cs)
  - [`RecurrentPaymentsController.cs`](../../src/Cnas.Ps.Api/Controllers/RecurrentPaymentsController.cs)
  - [`TreasuryPaymentsController.cs`](../../src/Cnas.Ps.Api/Controllers/TreasuryPaymentsController.cs)
  - [`TreasuryInformationController.cs`](../../src/Cnas.Ps.Api/Controllers/TreasuryInformationController.cs)
  - [`TreasuryFeedAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/TreasuryFeedAdminController.cs)
  - [`MPayCallbackController.cs`](../../src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs)
- Application services
  - `IPaymentSuspensionService`, `IMPayOrderStore`,
    `IRecurrentPaymentAdvancer`, `IRecurrentPaymentSchedulerService`.

## Data model

| Entity | Purpose |
|---|---|
| `BenefitPayment` | One instalment row per Decision per period. |
| `MPayOrder` | Outbound MPay order with idempotency key. |
| `PaymentSuspension` | Range over which a payment is suspended (death, recovery, custody). |
| `RecoveryDecision` | Cross-link to recovery flow when overpayment found. |

## Workflows

```
DecisionWorkflowService.IssueAsync → BenefitPayment[] rows created
                                          ↓
                                  RecurrentPaymentAdvancer
                                          ↓
                                  MPayDispatcherJob (every 5 min)
                                          ↓
                                  IMPayClient.PlaceOrderAsync
                                          ↓
                                  POST /api/mpay/confirm (inbound)
                                          ↓
                                  IMPayOrderStore.MarkConfirmedAsync (idempotent)
                                          ↓
                                  BenefitPayment.Status = Paid
                                          ↓
                                  AuditLog + MNotify + MCabinet
```

## Business rules

- The MPay confirm callback is **idempotent** — same external transaction
  id → no double-credit. `IMPayOrderStore` enforces a unique constraint on
  the MPay order id (CLAUDE.md "Idempotent callbacks").
- A suspension is a date range, not a flag — overlapping suspensions
  are union-collapsed in the calculation.
- Treasury feed ingestion reconciles bank-side acknowledgements; a
  payment with no matching feed line after 14 days raises an audit
  finding (`IntegrityCheckFinding`).
- Capitalised payments (single-lump) bypass the recurring scheduler —
  one `MPayOrder`, no `RecurrentPayment*`.

## Tests

- `tests/Cnas.Ps.Application.Tests/Benefits/`
- `tests/Cnas.Ps.Infrastructure.Tests/Services/MPayDispatcherJobTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/PaymentJourneyTests.cs`

## What's NOT here

- The MPay SOAP transport + XML-DSig outbound is externally gated — see
  [`mgov-integration.md`](mgov-integration.md).
- Bank file format adapters (treasury feed parsing) — covered under
  the migration path; the production parser ships with the SI FMS
  integration batch.
