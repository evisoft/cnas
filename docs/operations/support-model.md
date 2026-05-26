# Post-implementation support model

> Anchored to TOR ID(s): R2460 (DEL 014, Milestone M7). Conforms to
> SM ISO/CEI 14764:2015 (software maintenance). Iteration 103.

## 1. Purpose

Define the 12-month post-implementation support service: on-site +
remote, corrective + adaptive + preventive maintenance. Locks the
operating model to the platform's already-shipped helpdesk, SLA
evaluator and reporting surfaces.

## 2. Audience / stakeholders

- CNAS Service Owner and ops shift leads.
- Supplier support team (tier 1-3), DevOps, security officer.
- Acceptance committee for DEL 014.
- End-user community (raising tickets via the platform).

## 3. Support tiers and modes

| Tier | Mode | Responsibility |
|---|---|---|
| Tier 1 | Remote | Triage, classify, route. Confirm SLA timer started. |
| Tier 2 | Remote (+ on-site on demand) | Functional troubleshooting, configuration. |
| Tier 3 | Remote / on-site | Engineering fixes, hot-patches, post-mortems. |
| On-call | Remote, 24/7 for Critical | Pages on `Severity = Critical` ticket. |
| On-site | Per cadence in service schedule | Preventive maintenance + drills. |

Maintenance categories (per ISO/CEI 14764:2015):

- **Corrective** — defect fixes (raised as `SupportTicket`).
- **Adaptive** — adjustments to legislative/environment changes (raised
  as `ChangeRequest` via 4-eyes++ flow, iter 81/94).
- **Preventive** — proactive integrity, performance, capacity, and
  security hardening (`IntegrityCheckJob`, `AuditChainIntegrityCheck`,
  perf KPI watch).

## 4. Response (TR) and resolution (TS) per severity

Anchored to `SupportTicketCategory` + `SupportTicketSlaEvaluator`
(iter 92). Business hours: business days, 08:00-18:00 RM time
(iter 93).

| Severity | TR | TS | TOR |
|---|---|---|---|
| Critical | 5 min | 60 min | PIR 020 |
| High | 60 min | End of day | PIR 021 |
| Ordinary | 24 h | 3 business days | PIR 022 |
| Low | 3 business days | Best-effort | PIR 023 |

Breach behaviour: `SupportTicketSlaEvaluator` writes
`SupportTicketSlaEvent` rows; the `SupportTicketSlaEvaluationJob`
sweeps and auto-escalates on threshold (R2500).

## 5. Reporting

- Monthly support report (R2461): `IMonthlySupportReportService` ->
  template `monthly-support-report-template.md`.
- Monthly error-fix + doc-update report (R2462): template
  `monthly-error-fix-report-template.md`.
- Unplanned development burn-down (R2463): `docs/budget/unplanned-development-budget.md`.

## 6. Acceptance criteria

- TR/TS table above honoured for the full 12-month window.
- Monthly reports filed and signed by CNAS Service Owner.
- Quarterly DR drill executed (R2708, see `docs/dr/dr-drill-runbook.md`).
- 50 person-days unplanned development tracked (R2463).
- Sign-off entered in the Acceptance Protocol row "DEL 014 / R2460".

## 7. Status / open gaps

- On-call rotation roster: pending supplier + CNAS sign-off.
- 24/7 paging integration: depends on CNAS messaging gateway choice.
- Preventive cadence calendar: draft pending CNAS Service Owner review.

## 8. References

- TOR §DEL 014, §PIR 020-023, §PIR 025, §PIR 037-040, §PIR 041-043
- TODO.md R2460 (this row), R2461-R2463, R2500-R2507
- SM ISO/CEI 14764:2015
- [`monthly-support-report-template.md`](monthly-support-report-template.md)
- [`monthly-error-fix-report-template.md`](monthly-error-fix-report-template.md)
- [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md)
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)
