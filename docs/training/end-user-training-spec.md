# End-user training specification (up to 100 users, ≥40 hours)

> Anchored to TOR ID(s): R2443 (UTD 009, Milestone M5). Companion to
> R2440 ([`training-plan.md`](training-plan.md)). Iteration 103.

## 1. Purpose

Define the end-user training programme so CNAS, CTAS, and MMPS counter
staff can operate SI „Protecția Socială" in production. Locks in the
TOR-mandated quantitative thresholds: up to 100 end users, ≥ 40
instruction hours each, in Romanian and Russian.

## 2. Audience / stakeholders

- CNAS, CTAS, MMPS counter staff selected by CNAS HR (up to 100
  learners total across cohorts).
- Trainers certified under R2442 (≥ 4 internal trainers).
- Joint acceptance committee for UTD 009.

## 3. Curriculum and hours per module

| # | Module | Hours | Anchors |
|---|---|---|---|
| 1 | Portal navigation, MPass sign-in, language switcher | 4 | `docs/training/training-plan.md` §3.1 |
| 2 | Submitting `Cerere` — every benefit type, end-to-end | 10 | SRS §7, citizen-portal `applications/new` |
| 3 | Status follow-up, MSign sign-off, MNotify channels | 6 | Iter-99 MSign + MNotify integration specs |
| 4 | Document inbox, attachments, audit timeline | 4 | `inbox` page, audit chain (iter 95) |
| 5 | Practical scenarios per benefit family | 10 | Seeded staging data |
| 6 | Helpdesk usage (raising tickets, severity, SLA) | 2 | iter-92 `SupportTicketCategory` |
| 7 | Accessibility features, RO/RU switching tips | 2 | `Cnas.Ps.Accessibility.Tests` route table |
| 8 | Final assessment (written + practical) | 2 | This spec §4 |
| | **Total** | **40** | UTD 009 minimum |

## 4. Acceptance criteria

- Up to 100 learners enrolled across cohorts (R2443 ceiling).
- Each learner attended ≥ 40 hours.
- Attendance log signed per session by learner + trainer.
- Final assessment ≥ 75% per learner.
- Bilingual delivery (RO + RU) certified per cohort; learners receive
  materials in their declared language.
- Sign-off entered in the Acceptance Protocol row "UTD 009 / R2443".

## 5. Status / open gaps

- Named cohort lists and exact dates: pending CNAS HR confirmation.
- Slide decks, workbooks, recorded walkthroughs: pending (parent
  R2440).
- Final assessment item bank: pending.
- Per-benefit hands-on labs depend on seeded staging data being
  finalised.

## 6. References

- TOR §UTD 009
- TODO.md R2443 (this row), R2440 (parent plan)
- [`training-plan.md`](training-plan.md)
- [`admin-training-spec.md`](admin-training-spec.md)
- [`trainer-training-spec.md`](trainer-training-spec.md)
