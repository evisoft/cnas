# Stabilization plan — 3-month window

> Anchored to TOR ID(s): R2457 (STAB 001, Milestone M6). Iteration 101.
> Companion to [`final-acceptance.md`](final-acceptance.md),
> [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md) and
> [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md).

## 1. Purpose / scope

Defines the 3-month stabilization period that opens immediately after
the bilateral Acceptance Protocol is signed and closes only when the
final acceptance gate ([`final-acceptance.md`](final-acceptance.md)) is
green. Scopes the change envelope (P0/P1 fixes only), the defect-tracking
flow via `SupportTicket` (iter 92), and the change-management gates via
`ChangeRequest` (iter 81/94).

## 2. Audience / stakeholders

Supplier project manager, supplier engineering lead, supplier QA lead,
CNAS project director, CNAS QA lead, CNAS SRE, CNAS security officer,
joint change advisory board.

## 3. Procedure (numbered)

### 3.1 Timing

1. **Start.** The day the Acceptance Protocol (COM 004) is signed.
2. **Duration.** 3 calendar months (STAB 001).
3. **End.** The earlier of: 3 months elapsed AND
   [`final-acceptance.md`](final-acceptance.md) gate is green, OR a
   bilateral extension is signed.

### 3.2 Scope of changes allowed

| Class | Allowed | Examples |
|---|---|---|
| P0 (Critical) | Yes — immediate hotfix | Outage, data integrity break, security incident, missed statutory deadline. |
| P1 (Major) | Yes — scheduled within SLA | Benefit family broken for an audience, regression of a UAT-passing flow. |
| P2 (Minor) | Only if bundled with a P0/P1 change or in an explicit roll-up window | Cosmetic regressions, UX friction without workaround. |
| P3 (Cosmetic) | Deferred to post-stabilization unless trivial | Copy fixes, layout polish. |
| Feature work | **Not allowed** | New capabilities outside the accepted M6 envelope are deferred to PIR (Phase 13 +). |

`SupportTicketSeverity` mapping: P0 = `Critical`, P1 = `High`, P2 =
`Normal`, P3 = `Low`
(`src/Cnas.Ps.Core/Domain/Enums.cs`).

### 3.3 Defect tracking flow

1. Every defect filed during stabilization opens a `SupportTicket`
   record via `ISupportTicketService` with the correct severity from
   §3.2.
2. Severity is owned by CNAS QA; the supplier may propose a different
   severity but the CNAS-side label binds.
3. SLA timers attached to the ticket category (`SupportTicketCategory`,
   iter 92) drive escalations and the monthly support report
   (`IMonthlySupportReportService`, iter 96).
4. Resolution rolls into a `ChangeRequest` via the iter 81/94 4-eyes++
   workflow (Draft → Submitted → InReview → TestEnvValidated →
   CodeSigned → ApprovedForProd → Deploying → Deployed, optionally
   RolledBack).

### 3.4 Change-management gates

| Gate | Surface | Required signatures |
|---|---|---|
| Test-environment validation | `IChangeRequestService.MarkTestEnvValidatedAsync` | Independent tester (not the requester) |
| Code signature | `IChangeRequestService.MarkCodeSignedAsync` | Code signer (separate from requester + tester) |
| Production approval | `IChangeRequestService.ApproveForProdAsync` | Approver (separate from all of the above) |
| Deployment | `IChangeRequestService.MarkDeployedAsync` | Deploy operator |
| Rollback (if required) | `IChangeRequestService.RollBackAsync` | Same four-roles separation; rollback referenced in `docs/production-deployment.md` |

User-visible deployments are bracketed by a `MaintenanceWindow` of the
appropriate class (Ordinary 5BD / Major 10BD / Urgent immediate) per
iter 94.

### 3.5 Monthly cadence

1. End of each stabilization month: emit the monthly support report
   (`/api/admin/reporting/support-monthly`, iter 96) and the monthly
   error-fix + doc-update report
   (`/api/admin/reporting/error-fix-monthly`, iter 96).
2. Joint change advisory board meets monthly to review reports,
   re-triage open tickets, and re-confirm severity classifications.
3. Joint risk register reviewed monthly; new risks open a
   `QualityRisk` via `IQualityRiskService`.

### 3.6 Integrity assurance

1. `IntegrityCheckJob` (iter 76) runs nightly and must complete with
   zero unacknowledged Critical findings throughout the window.
2. `AuditChainIntegrityCheck` (iter 95) runs as part of the integrity
   sweep; any break triggers a P0 ticket.

## 4. Acceptance criteria / sign-off

- Defect bar maintained for the duration (see
  [`final-acceptance.md`](final-acceptance.md) for the closing gate).
- Every production change in the window has a traceable `ChangeRequest`
  with the 4-eyes++ signatures recorded.
- Three monthly reports archived (support + error-fix).
- Joint sign-off at the end of month 3 closes the window.

## 5. Implementation map

| Capability | Where |
|---|---|
| Defect aggregate | `src/Cnas.Ps.Core/Domain/SupportTicket.cs` |
| Defect service | `src/Cnas.Ps.Infrastructure/Services/Helpdesk/SupportTicketService.cs` |
| Severity enum | `src/Cnas.Ps.Core/Domain/Enums.cs` (`SupportTicketSeverity`) |
| Change-management workflow | `src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs` |
| Maintenance windows | `src/Cnas.Ps.Application/ServiceManagement/IMaintenanceWindowService.cs` |
| Integrity sweep | `src/Cnas.Ps.Infrastructure/Jobs/IntegrityCheckJob.cs` |
| Audit chain re-check | `src/Cnas.Ps.Infrastructure/Services/Integrity/Checks/AuditChainIntegrityCheck.cs` |
| Monthly reports | `src/Cnas.Ps.Application/Reporting/IMonthlySupportReportService.cs`, `IMonthlyErrorFixReportService.cs` |

## 6. Status / open gaps

- Joint change advisory board charter — pending CNAS sign-off.
- Stabilization-window communication plan to end users — pending.
- Stabilization-period KPI dashboard wiring — pending.

## 7. References

- TOR §STAB 001
- TODO.md row R2457
- [`final-acceptance.md`](final-acceptance.md)
- [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md)
- [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)
- [`../production-deployment.md`](../production-deployment.md)
- [`../recovery-procedures.md`](../recovery-procedures.md)
