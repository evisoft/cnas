# DEL 006 — Signed SLA agreement (index + template skeleton)

> Anchored to TOR ID(s): R2605 (TOR §7.1). Index doc plus the SLA
> template skeleton bilateral parties fill in. Iteration 103.

## 1. Purpose

Single navigation surface for the SLA deliverable required by TOR
§7.1 DEL 006. Bundles the SLA template skeleton with response /
resolution times anchored to the platform's already-shipped helpdesk
SLA evaluator.

## 2. Audience

CNAS contracting authority, supplier authorised signatory, CNAS
Service Owner, supplier support lead, audit reviewers.

## 3. SLA template skeleton

Fillable bilateral SLA — the supplier supplies the table below as the
operational annex; bilateral signatures finalise DEL 006.

### 3.1 Parties

- Beneficiary: CNAS „Casa Națională de Asigurări Sociale".
- Supplier: `<supplier legal name>`.
- Effective period: from `<go-live>` through `<go-live + 12 months>`
  (M7 window).

### 3.2 Response (TR) and resolution (TS) targets

Anchored to `SupportTicketCategory` + `SupportTicketSlaEvaluator`
(iter 92). Business hours: business days, 08:00-18:00 RM time
(iter 93).

| Severity | TR | TS | TOR row |
|---|---|---|---|
| Critical | 5 min | 60 min | PIR 020 |
| High | 60 min | End of day | PIR 021 |
| Ordinary | 24 h | 3 business days | PIR 022 |
| Low | 3 business days | Best-effort | PIR 023 |

### 3.3 Maintenance windows

| Window | Max duration | Notice | TOR row |
|---|---|---|---|
| Ordinary | 4 h | 5 business days | PIR 025 |
| Major | 24 h | 10 business days | PIR 025 |
| Urgent | 2 h | Immediate notice | PIR 025 |

### 3.4 Reporting cadence

- Monthly support report (R2461).
- Monthly error-fix + doc-update report (R2462).
- Unplanned development burn-down (R2463).

### 3.5 Penalty clauses

`<bilateral fill — penalties for sustained SLA breach; reference
breach-rate fields from IMonthlySupportReportService>`.

### 3.6 Signatures

| Role | Name | Date | Signature |
|---|---|---|---|
| CNAS contracting authority | | | |
| Supplier authorised signatory | | | |
| CNAS Service Owner (witness) | | | |
| Supplier support lead (witness) | | | |

## 4. Acceptance criteria

- All bracketed `<placeholders>` filled before signing.
- TR / TS / maintenance-window rows match TOR §PIR 020-025 verbatim.
- Bilateral signatures present.
- Sign-off entered in the Acceptance Protocol row "DEL 006 / R2605".

## 5. Status / open gaps

- Penalty clause text: bilateral fill, pending negotiation.
- Effective date: pending go-live confirmation.
- Names and signatures: pending bilateral signing ceremony.

## 6. References

- TOR §7.1 DEL 006, §PIR 020-025
- TODO.md R2605 (this row), R2500-R2502
- [`../operations/support-model.md`](../operations/support-model.md)
- [`../operations/monthly-support-report-template.md`](../operations/monthly-support-report-template.md)
