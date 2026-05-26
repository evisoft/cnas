# Unplanned development budget — 50 person-days

> Anchored to TOR ID(s): R2463 (PIR 018, Phase 15). Iteration 102.
> Anchored to existing helpdesk + change-management iterations:
> iter 92 (`SupportTicket`) and iter 94 (`ChangeRequest` 4-eyes++).

## 1. Purpose / scope

PIR 018 requires the supplier to include **50 person-days of unplanned
development** in the contract envelope, drawn on by CNAS during the
post-implementation support period for work that is not already
covered by corrective maintenance. This document defines the tracking
process, the burn-down view, the approval thresholds, and the operating
artefacts that record consumption. Scope = the 12-month
post-implementation support window (M7); resets if a follow-on
contract extends it.

## 2. Audience / stakeholders

CNAS operations lead, CNAS contracting authority, supplier
service-delivery manager, supplier engineering lead, supplier project
manager.

## 3. Content + procedure

### 3.1 What counts as unplanned development

| Counts | Does not count |
|---|---|
| New feature requested by CNAS post-go-live not in the original SRS | Corrective maintenance (defect fixes) — that lives in the support budget. |
| Modifications to existing features beyond the support scope | Routine operational tasks (cert rotation, capacity tuning) — that lives in the support budget. |
| Adapter changes driven by partner-system schema changes | Partner integrations explicitly out of scope at signing. |
| Reporting / dashboard additions beyond the SRS catalogue | Adaptive maintenance compelled by RM regulatory change — billed separately under R2460 adaptive line. |

### 3.2 How unplanned dev is logged

Each unit of unplanned work is initiated as a **SupportTicket** with
category `UNPLANNED_DEVELOPMENT` (or a sub-category under the project
support taxonomy). The ticket carries:

- `Description` — what is requested.
- `EstimatedPersonDays` — supplier's effort estimate.
- `RequestedBy` — CNAS originator.

Once approved (see §3.4), the ticket is promoted to a **ChangeRequest**
via the existing 4-eyes++ workflow (iter 94):

- Draft → Submitted → Reviewed → Approved → Deployed → (optionally) RolledBack.
- The `ChangeRequest.LinkedSupportTicketId` field carries the back-reference.
- `ActualPersonDays` is recorded at Deployed.

Both `SupportTicket` and `ChangeRequest` aggregates already exist —
no new code is introduced by this document.

### 3.3 Burn-down view

The supplier service-delivery manager maintains a monthly burn-down
table, derived from the SupportTicket + ChangeRequest aggregates,
published alongside the monthly support report
([`../operations/monthly-support-report-template.md`](../operations/monthly-support-report-template.md)).

```markdown
| Month | Approved (PD) | Consumed (PD) | Remaining (PD, of 50) | Notes |
|---|---|---|---|---|
| M+01 | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| M+02 | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| …    | …                    | …                    | …                                | …                    |
| M+12 | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |

**Cumulative consumed:** <!-- placeholder: sum -->
**Remaining headroom:** <!-- placeholder: 50 - cumulative -->
```

### 3.4 Approval thresholds

| Estimated effort | Approver |
|---|---|
| ≤ 5 person-days | CNAS operations lead — same channel as standard ChangeRequest 4-eyes++. |
| > 5 person-days | **Additional CNAS contracting-authority sign-off required** before promotion from SupportTicket to ChangeRequest. |
| Cumulative remaining headroom < 10 PD | Supplier raises an early-warning notice; new ChangeRequests above 5 PD blocked until CNAS confirms reserve usage strategy. |
| Cumulative remaining headroom = 0 PD | Further unplanned work falls outside contract envelope and requires a contract amendment. |

### 3.5 Reporting cadence

- **Monthly** — burn-down row added to the monthly support report.
- **Quarterly** — joint review meeting (CNAS + supplier) reconciling estimated vs actual person-days, decision on remaining headroom.
- **At contract end** — final burn-down published as part of [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md); residual headroom is forfeited unless contractually rolled over.

### 3.6 Closure of an unplanned-dev item

A SupportTicket → ChangeRequest pair is considered closed when:

1. ChangeRequest reaches `Deployed`.
2. `ActualPersonDays` is recorded.
3. Burn-down table is updated.
4. CNAS operations lead signs the row in the next monthly report.

## 4. Acceptance criteria

- Contract envelope explicitly includes 50 person-days of unplanned development.
- A SupportTicket category `UNPLANNED_DEVELOPMENT` exists in the support taxonomy at contract start.
- Every consumed person-day is traceable back to a SupportTicket + ChangeRequest pair.
- Cumulative consumed ≤ 50 PD over the support window, or a contract amendment exists for any overage.
- Monthly burn-down table appears in every monthly support report from M+01.
- Approval thresholds (§3.4) enforced — verifiable in the ChangeRequest audit chain.

## 5. Implementation map

| Surface | Path |
|---|---|
| SupportTicket aggregate (iter 92) | `src/Cnas.Ps.Core/Support/SupportTicket.cs` (target path) |
| SupportTicket category | `SupportTicketCategory` (taxonomy seed) |
| ChangeRequest aggregate (iter 81/94) | `src/Cnas.Ps.Core/Changes/ChangeRequest.cs` (target path) |
| ChangeRequest 4-eyes++ flow | `IChangeRequestService` (Draft→Submitted→Reviewed→Approved→Deployed→RolledBack) |
| Monthly support report (R2461) | [`../operations/monthly-support-report-template.md`](../operations/monthly-support-report-template.md) |
| Contract-end procedures | [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) |

## 6. Status / open gaps

- `UNPLANNED_DEVELOPMENT` category not yet seeded into `SupportTicketCategory`. TODO follow-up (data migration).
- `SupportTicket.EstimatedPersonDays` / `ChangeRequest.ActualPersonDays` fields are target-state; verify presence in the current model before contract sign-off.
- Burn-down table is not yet generated automatically by `IMonthlySupportReportService`; report owners produce it manually until a derived metric is added.
- Contract amendment template for headroom overage — owned by CNAS contracting authority.

## 7. References

- TOR §PIR 018
- TODO.md row R2463
- TOR §DEL 014 (R2460 — corrective + adaptive + preventive maintenance)
- [`../operations/monthly-support-report-template.md`](../operations/monthly-support-report-template.md) (R2461)
- [`../operations/monthly-error-fix-report-template.md`](../operations/monthly-error-fix-report-template.md) (R2462)
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)
