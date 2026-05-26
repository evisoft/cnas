# Feature — Contribuții sociale (Contributions & declarations)

## What it is

Declaration intake (REV5 and successors), period-level contribution
aggregation per insured person, late-payment penalty calculation and
repayment plans, BASS refunds. The accounting heart of the system.

## TOR / UC mapping

- **UC07** — Înregistrare formular (declarations).
- Annex 1 — BP-2, BP-3 (declarations + reconciliations).
- TOR clauses: CF 07.*, MR 004.

## Surface

| Endpoint | Auth | Limiter |
|---|---|---|
| `POST /api/declarations` | `CnasDecider` | `Authenticated` |
| `GET /api/declarations/{sqid}` | `CnasUser` | `Authenticated` |
| `POST /api/rev5-declarations/import` | `CnasDecider` | `Upload` |
| `GET /api/late-penalty/calculate` | `CnasUser` | `Authenticated` |
| `POST /api/penalty-repayment-plans` | `CnasDecider` | `Authenticated` |
| `POST /api/bass-refunds` | `CnasDecider` | `Authenticated` |

## Code map

- Controllers
  - [`DeclarationsController.cs`](../../src/Cnas.Ps.Api/Controllers/DeclarationsController.cs)
  - [`Rev5DeclarationsController.cs`](../../src/Cnas.Ps.Api/Controllers/Rev5DeclarationsController.cs)
  - [`LatePenaltyController.cs`](../../src/Cnas.Ps.Api/Controllers/LatePenaltyController.cs)
  - [`PenaltyRepaymentPlansController.cs`](../../src/Cnas.Ps.Api/Controllers/PenaltyRepaymentPlansController.cs)
  - [`BassRefundsController.cs`](../../src/Cnas.Ps.Api/Controllers/BassRefundsController.cs)
- Application services — `IFisaDeCalculRecalculator` (recompute),
  declaration validators (FluentValidation) under
  `src/Cnas.Ps.Application/Validators/`.

## Data model

| Entity | Purpose |
|---|---|
| `Declaration` | One header row per declaration submission. |
| `ContributorPeriodProjection` | Per-payer / per-period rollup used by recompute. |
| `InsuredPersonContributionAdjustment` | Per-person impact of a declaration. |
| `LatePaymentPenalty` | Calculated penalty per overdue period. |
| `BassRefund` | Refund from the social-insurance budget back to a payer. |

## Business rules

- Declaration import is **idempotent** — same content hash → same
  outcome. Duplicate uploads short-circuit with `DECLARATION.DUPLICATE`.
- Penalty calculation snapshots the rate **at the time of overdue
  recognition** (CLAUDE.md "Immutable snapshots"). Subsequent rate
  changes don't retro-apply.
- A repayment plan is a contract — `PenaltyRepaymentPlan` rows freeze
  the schedule when signed. Missed instalments accrue further penalty
  via the same calculator with a flag indicating "second-order".
- REV5 import accepts up to `MaxFileSizeBytes` (25 MiB default) and is
  rate-limited on the `Upload` partition (10/60 s, queue 2).

## Tests

- `tests/Cnas.Ps.Application.Tests/Declarations/`
- `tests/Cnas.Ps.Application.Tests/Penalties/`
- `tests/Cnas.Ps.Infrastructure.Tests/Services/Rev5DeclarationImporterTests.cs`

## What's NOT here

- The penalty rate **table** is read from `Classifier` rows — see
  [`service-passport.md`](service-passport.md) for catalog admin.
- Treasury settlement of refunded amounts lives in
  [`payments.md`](payments.md).
