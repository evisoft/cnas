# DEL 008..014 — Knowledge transfer + post-implementation support (index)

> Anchored to TOR ID(s): R2607 (TOR §7.1). Index doc covering the
> remaining DEL bundle: knowledge transfer to CNAS engineering /
> ops, then 12 months of post-implementation support. Iteration 103.

## 1. Purpose

Single navigation surface for the knowledge-transfer and
post-implementation-support deliverables (DEL 008 through DEL 014).
Cross-links the source-code handover, the contract-end procedures
and the support model.

## 2. Audience

CNAS engineering lead, CNAS Service Owner, CNAS DevOps + security
officer, supplier engineering lead + support lead, successor supplier
(at contract end), acceptance committee.

## 3. Bundle contents

| Deliverable | Artefact | File | TOR row |
|---|---|---|---|
| DEL 008 — Source code + repo access handover | Source-code handover plan | [`../handover/source-code-handover.md`](../handover/source-code-handover.md) | R2445 / UTD 014 |
| DEL 009 — Admin transfer (knowledge) | Admin training spec | [`../training/admin-training-spec.md`](../training/admin-training-spec.md) | R2441 / UTD 007 |
| DEL 010 — Operations transfer (knowledge) | Operational guides index | [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md) | R2444 / UTD 013 |
| DEL 011 — Trainer transfer (train-the-trainer) | Trainer training spec | [`../training/trainer-training-spec.md`](../training/trainer-training-spec.md) | R2442 / UTD 008 |
| DEL 012 — End-user transfer | End-user training spec | [`../training/end-user-training-spec.md`](../training/end-user-training-spec.md) | R2443 / UTD 009 |
| DEL 013 — Contract-end procedures | Contract-end plan | [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) | R2507 / PIR 041-043 |
| DEL 014 — Post-implementation support (12 mo) | Support model | [`../operations/support-model.md`](../operations/support-model.md) | R2460 / DEL 014 |

## 4. Cross-cutting hooks

- Monthly support report (R2461): `IMonthlySupportReportService` ->
  [`../operations/monthly-support-report-template.md`](../operations/monthly-support-report-template.md).
- Monthly error-fix + doc-update report (R2462): `IMonthlyErrorFixReportService`
  -> [`../operations/monthly-error-fix-report-template.md`](../operations/monthly-error-fix-report-template.md).
- Unplanned development burn-down (R2463):
  [`../budget/unplanned-development-budget.md`](../budget/unplanned-development-budget.md).
- Quarterly DR drill: [`../dr/dr-drill-runbook.md`](../dr/dr-drill-runbook.md).
- Stabilization gate (3 months): [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md).
- IP / data-ownership transfer: [`../contract/ip-transfer.md`](../contract/ip-transfer.md),
  [`../contract/data-ownership-nda-dpa.md`](../contract/data-ownership-nda-dpa.md).

## 5. Acceptance criteria

- All linked artefacts above resolve and are signed at the
  appropriate handover ceremony.
- Knowledge-transfer sessions logged (attendance + topics) per UTD
  007 / UTD 008 / UTD 009 / UTD 013 / UTD 014.
- 12-month support window operates per R2460 (this iter), with
  monthly reports filed and signed.
- Contract-end retention + 1-year successor cooperation honoured per
  R2507.
- Sign-off entered in the Acceptance Protocol rows for each DEL.

## 6. Status / open gaps

- DEL 010 admin-feature guide stubs unfilled
  (`operational-guides-index.md` §3.10).
- DEL 012 video walkthroughs (UTD 011-012) pending recording.
- DEL 014 on-call rotation roster pending bilateral sign-off.

## 7. References

- TOR §7.1 DEL 008-014, §UTD 007-009 / 013 / 014, §PIR 041-043
- TODO.md R2607 (this row), R2441-R2445, R2460-R2463, R2507
- [`../handover/`](../handover/), [`../training/`](../training/),
  [`../operations/`](../operations/), [`../contract/`](../contract/)
