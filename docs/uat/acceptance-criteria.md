# UAT acceptance criteria — defect bar

> Anchored to TOR ID(s): R2453 (UAT 006, Milestone M6). Iteration 101.
> Companion to [`uat-plan.md`](uat-plan.md),
> [`uat-joint-tests.md`](uat-joint-tests.md) and
> [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md).

## 1. Purpose / scope

Defines the **bug-bar formula** that determines whether the supplier
deliverable is accepted at the close of UAT: **0 Critical defects + < 3
Major defects** open at sign-off. Includes the severity classification
that maps to the `SupportTicketSeverity` enum (iter 92) so a UAT-time
defect and a post-go-live `SupportTicket` carry the same label.

## 2. Audience / stakeholders

Supplier QA lead, supplier project manager, CNAS QA observer, CNAS
project director, joint acceptance committee.

## 3. Procedure (numbered)

### 3.1 Severity classification

| Label | Maps to `SupportTicketSeverity` | Definition |
|---|---|---|
| Critical | `Critical` (= 3) | Production-equivalent outage; data integrity loss; security incident; statutory deadline missed. Minute-grade response. |
| Major | `High` (= 2) | Significant business impact; a benefit family or core admin surface unusable for the affected role; same-day target. |
| Minor | `Normal` (= 1) | Defect with workaround; non-blocking. Default category severity for routine requests. |
| Cosmetic | `Low` (= 0) | Visual / copy issue; no functional impact. Best-effort, no firm SLA target. |

The enum is defined in
`src/Cnas.Ps.Core/Domain/Enums.cs` (`SupportTicketSeverity`).

### 3.2 Bug-bar formula (UAT 006)

The deliverable passes UAT iff at sign-off time:

```
critical_open == 0
  AND
major_open  <  3
```

Both counts are taken from the joint UAT defect tracker (or the
underlying `SupportTicket` table once the bilateral tracker mirrors
into it).

### 3.3 Counting rules

1. **Open** = ticket state is not Resolved, not Closed, not Cancelled.
2. **Workarounds** do not downgrade severity. If a workaround exists,
   the supplier may negotiate downgrade with the CNAS QA lead and
   record the rationale on the ticket.
3. **Duplicates** collapse into the originating ticket; the dup is
   linked, not counted.
4. **Out-of-scope** tickets (feature requests, scope-creep items) are
   labelled `OutOfScope` and excluded from the count; CNAS QA signs
   off on every out-of-scope label.
5. **Stabilization spill-over** — any Minor/Cosmetic ticket open at
   sign-off rolls into the M6 stabilization window per
   [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md).

### 3.4 Resolution evidence

1. Each Critical/Major defect resolution must reference a
   `ChangeRequest` (iter 81/94) traversing the 4-eyes++ workflow.
2. Audit-related defects must be re-validated by
   `AuditChainIntegrityCheck` (iter 95) running green on the supplier
   branch and the CNAS clone.
3. Integrity-related defects must be re-validated by
   `IntegrityCheckJob` (iter 76) completing with zero open Critical
   findings.

## 4. Acceptance criteria / sign-off

- Bug-bar formula in §3.2 evaluates to **true**.
- All Critical/Major resolutions have a green `ChangeRequest`.
- Integrity and audit-chain checks green on the supplier branch.
- Supplier and CNAS QA leads counter-sign the joint defect register.
- The Acceptance Protocol
  ([`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md))
  is signed by both project leaders.

## 5. Implementation map

| Capability | Where |
|---|---|
| Severity enum | `src/Cnas.Ps.Core/Domain/Enums.cs` (`SupportTicketSeverity`) |
| Defect aggregate | `src/Cnas.Ps.Core/Domain/SupportTicket.cs`, service in `src/Cnas.Ps.Infrastructure/Services/Helpdesk/` |
| Change-management workflow | `src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs` |
| Integrity gate | `src/Cnas.Ps.Infrastructure/Jobs/IntegrityCheckJob.cs` |
| Audit chain re-check | `src/Cnas.Ps.Infrastructure/Services/Integrity/Checks/AuditChainIntegrityCheck.cs` |

## 6. Status / open gaps

- Joint UAT defect tracker — pending selection (whether to use the
  in-product `SupportTicket` table or an external bilateral tool that
  mirrors into it).
- Out-of-scope label policy — pending CNAS sign-off.
- Mapping of "Major" workaround downgrades — pending CNAS QA approval
  matrix.

## 7. References

- TOR §UAT 006
- TODO.md row R2453
- [`uat-plan.md`](uat-plan.md)
- [`uat-joint-tests.md`](uat-joint-tests.md)
- [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)
- [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md)
