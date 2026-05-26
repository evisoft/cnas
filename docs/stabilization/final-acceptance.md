# Final acceptance — stabilization gate

> Anchored to TOR ID(s): R2458 (STAB 004, Milestone M6). Iteration 101.
> Companion to [`stabilization-plan.md`](stabilization-plan.md),
> [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md) and
> [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md).

## 1. Purpose / scope

Defines the closing gate of the 3-month stabilization window required
by STAB 004: **all L1 fixed, < 10 L2 open, no integrity issues**. Maps
the L1 / L2 / L3 levels to `SupportTicketSeverity`, and binds the
"no integrity issues" rule to the existing `IntegrityCheckJob` (iter
76) and `AuditChainIntegrityCheck` (iter 95) signals.

## 2. Audience / stakeholders

CNAS project director, supplier project manager, CNAS QA lead,
supplier QA lead, CNAS security officer, joint change advisory board.

## 3. Procedure (numbered)

### 3.1 Level classification

| Level | Maps to `SupportTicketSeverity` | Definition |
|---|---|---|
| L1 | `Critical` (= 3) | Production-equivalent failure; statutory deadline missed; security incident; data integrity break. Must be fully closed (`Resolved` or `Closed`) at gate time. |
| L2 | `High` (= 2) | Significant impact, workaround may exist; partial-feature regression. Counted as open if not `Resolved` / `Closed` / `Cancelled`. |
| L3 | `Normal` or `Low` (<= 1) | Minor or cosmetic. Not counted in the gate; tracked into post-stabilization PIR. |

The enum is defined in `src/Cnas.Ps.Core/Domain/Enums.cs`.

### 3.2 Final-acceptance gate formula

The stabilization window closes successfully iff at gate-evaluation
time:

```
open_L1 == 0
  AND
open_L2 < 10
  AND
integrity_findings_open(Critical) == 0
  AND
audit_chain_last_run == green
```

`integrity_findings_open(Critical)` is the count of unacknowledged
`IntegrityCheckFinding` rows with severity Critical (from the latest
`IntegrityCheckRun`).

`audit_chain_last_run == green` means the most recent
`AuditChainIntegrityCheck` execution reports zero broken rows
(`FirstBrokenRowId` is null).

### 3.3 Evidence package

1. `SupportTicket` snapshot exported (CSV + audit summary) showing the
   tally evaluated by §3.2.
2. Latest monthly support report
   (`/api/admin/reporting/support-monthly`, iter 96).
3. Latest monthly error-fix + doc-update report
   (`/api/admin/reporting/error-fix-monthly`, iter 96).
4. Last 30 days of `IntegrityCheckRun` summaries (totals + finding
   counts per severity).
5. `AuditChainIntegrityCheck` last-run report (must be green, see
   §3.2).
6. List of any L3 items intentionally deferred to PIR.

### 3.4 Closing ceremony

1. Joint change advisory board confirms the formula evaluates to
   **true** with the evidence in §3.3.
2. The Acceptance Protocol
   ([`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md))
   is updated with a "Stabilization closed" appendix and counter-signed.
3. Outstanding L2 items (< 10) and any L3 items are formally rolled
   into PIR (Phase 13+, post-implementation support per R2460).
4. The system transitions out of the stabilization regime; PIR support
   tiers + SLAs (PIR 020-023) become the steady-state contract.

### 3.5 Failure-to-close path

1. If §3.2 evaluates to **false** at the planned closing date, the
   window is extended by mutual signature.
2. Root-cause analysis is filed via the change-management workflow
   (`IChangeRequestService`) with the "stabilization extension" flag.
3. New monthly cadence applies until the gate is green.

## 4. Acceptance criteria / sign-off

- Formula in §3.2 evaluates to **true** with the evidence in §3.3.
- Appendix to the Acceptance Protocol counter-signed.
- L2/L3 backlog forwarded to PIR with owners and target dates.
- The closing-ceremony minutes are archived next to the signed
  Acceptance Protocol appendix.

## 5. Implementation map

| Capability | Where |
|---|---|
| Defect aggregate | `src/Cnas.Ps.Core/Domain/SupportTicket.cs` |
| Severity enum | `src/Cnas.Ps.Core/Domain/Enums.cs` (`SupportTicketSeverity`) |
| Integrity job | `src/Cnas.Ps.Infrastructure/Jobs/IntegrityCheckJob.cs` |
| Audit chain check | `src/Cnas.Ps.Infrastructure/Services/Integrity/Checks/AuditChainIntegrityCheck.cs` |
| Audit chain verifier | `src/Cnas.Ps.Application/Audit/IAuditChainVerifier.cs` |
| Monthly support report | `src/Cnas.Ps.Application/Reporting/IMonthlySupportReportService.cs` |
| Monthly error-fix report | `src/Cnas.Ps.Application/Reporting/IMonthlyErrorFixReportService.cs` |
| Change-management workflow | `src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs` |

## 6. Status / open gaps

- Automated "final-acceptance dashboard" tile rendering §3.2 in
  real time — pending (not on the iter 101 envelope).
- L2 count export-to-PIR pipeline — pending; today done manually.
- Stabilization-extension template wording — pending CNAS legal.

## 7. References

- TOR §STAB 004
- TODO.md row R2458
- [`stabilization-plan.md`](stabilization-plan.md)
- [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md)
- [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)
- [`../recovery-procedures.md`](../recovery-procedures.md)
- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)
