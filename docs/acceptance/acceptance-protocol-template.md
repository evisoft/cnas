# Acceptance Protocol — template

> Anchored to TOR ID(s): R2456 (COM 004, Milestone M6). Iteration 101.
> This is a **template**: every `<!-- placeholder -->` must be filled
> when the document is printed for signature. Companion to
> [`../uat/uat-plan.md`](../uat/uat-plan.md),
> [`../uat/uat-joint-tests.md`](../uat/uat-joint-tests.md) and
> [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md).

## 1. Purpose / scope

This template captures the bilateral acceptance signing event between
the supplier and CNAS at the close of UAT for SI „Protecția Socială".
A signed instance of this template is the binding acceptance artefact
required by COM 004 before the system enters the M6 stabilization
3-month window
([`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md)).

## 2. Audience / stakeholders

CNAS project director, supplier project manager, CNAS QA lead,
supplier QA lead, CNAS security officer, supplier security officer,
joint acceptance committee, observers.

---

## 3. Acceptance Protocol — fillable form

### 3.1 Identification

| Field | Value |
|---|---|
| Protocol ID | <!-- placeholder: ACC-YYYY-NN --> |
| Project | SI „Protecția Socială" |
| Contract reference | <!-- placeholder: contract no. / date --> |
| Acceptance scope | <!-- placeholder: e.g. "Milestone M6 acceptance" --> |
| Date of signing | <!-- placeholder: YYYY-MM-DD --> |
| Location | <!-- placeholder --> |

### 3.2 Stakeholders

| Role | Name | Organisation | Signature | Date |
|---|---|---|---|---|
| CNAS project director | <!-- placeholder --> | CNAS | __________ | __________ |
| Supplier project manager | <!-- placeholder --> | <!-- placeholder --> | __________ | __________ |
| CNAS QA lead | <!-- placeholder --> | CNAS | __________ | __________ |
| Supplier QA lead | <!-- placeholder --> | <!-- placeholder --> | __________ | __________ |
| CNAS security officer | <!-- placeholder --> | CNAS | __________ | __________ |
| Supplier security officer | <!-- placeholder --> | <!-- placeholder --> | __________ | __________ |

### 3.3 Deliverable checklist

| # | Deliverable | TOR row | Evidence (file / report) | Status |
|---|---|---|---|---|
| 1 | UAT supplier-led report (5 types) | UAT 003 / R2450 | <!-- placeholder --> | [ ] Accepted |
| 2 | UAT joint test report (3 types) | UAT 004 / R2451 | <!-- placeholder --> | [ ] Accepted |
| 3 | Coverage report >= 90% | UAT 005 / R2452 | <!-- placeholder: coverlet report --> | [ ] Accepted |
| 4 | Defect bar met (0 Critical + < 3 Major) | UAT 006 / R2453 | <!-- placeholder: defect register snapshot --> | [ ] Accepted |
| 5 | Production deployment + rollback plan | COM 002 / R2455 | `docs/production-deployment.md` | [ ] Accepted |
| 6 | Go-live strategy signed | COM 001 / R2454 | `docs/go-live-strategy.md` | [ ] Accepted |
| 7 | Migration acceptance protocol signed | DEL 4.3 / R2434 | `docs/migration/migration-acceptance-protocol.md` | [ ] Accepted |
| 8 | Training delivered to required headcounts | UTD 007-009 | `docs/training/training-plan.md` + attendance | [ ] Accepted |
| 9 | Operational guides delivered | UTD 013 / R2444 | `docs/operations/operational-guides-index.md` | [ ] Accepted |
| 10 | Source code + repo access handed over | UTD 014 / R2445 | `docs/handover/source-code-handover.md` | [ ] Accepted |
| 11 | Audit chain green | SEC 047 / R0194 | `AuditChainIntegrityCheck` report | [ ] Accepted |
| 12 | Integrity sweep green | SEC 036 / R2282 | `IntegrityCheckJob` last-run report | [ ] Accepted |
| 13 | BCP / DRP / Backup plan | R2459 | `docs/bcp-drp-backup-plan.md` | [ ] Accepted |
| 14 | Recovery procedures | SEC 063 / SEC 066 | `docs/recovery-procedures.md` | [ ] Accepted |

### 3.4 Outstanding items (rolled into stabilization)

| # | Item | Severity | Owner | Target |
|---|---|---|---|---|
| 1 | <!-- placeholder --> | Minor / Cosmetic | <!-- placeholder --> | <!-- placeholder --> |

### 3.5 Stabilization window commencement

| Field | Value |
|---|---|
| Start date | <!-- placeholder: YYYY-MM-DD --> |
| End date (start + 3 months) | <!-- placeholder --> |
| Plan reference | [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md) |

### 3.6 Sign-off statement

> We, the undersigned, confirm that the deliverables listed in §3.3 are
> accepted as defined by TOR row COM 004 and that the defect bar
> defined in [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md)
> has been met. The system enters the stabilization window on the date
> recorded in §3.5.

### 3.7 Final signatures

| Party | Name | Signature | Date |
|---|---|---|---|
| For CNAS | <!-- placeholder --> | __________ | __________ |
| For supplier | <!-- placeholder --> | __________ | __________ |

---

## 4. Acceptance criteria / sign-off

- Every row in §3.3 marked Accepted or formally waived.
- Defect bar from [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md)
  evaluates to true at signing time.
- §3.4 outstanding-items table forwarded to the M6 stabilization
  tracker (`SupportTicket`, iter 92).
- Counter-signatures present in §3.7.

## 5. Implementation map

| Surface | Where |
|---|---|
| Severity enum | `src/Cnas.Ps.Core/Domain/Enums.cs` |
| Defect aggregate | `src/Cnas.Ps.Core/Domain/SupportTicket.cs` |
| Change-management workflow | `src/Cnas.Ps.Application/ServiceManagement/IChangeRequestService.cs` |
| Integrity gate | `src/Cnas.Ps.Infrastructure/Jobs/IntegrityCheckJob.cs` |

## 6. Status / open gaps

- Template owners (CNAS legal + supplier project office) — pending
  approval of the wording in §3.6.
- Signature-page layout for the printed PDF — pending.
- RU translation of the printed form — pending.

## 7. References

- TOR §COM 004
- TODO.md row R2456
- [`../uat/uat-plan.md`](../uat/uat-plan.md)
- [`../uat/uat-joint-tests.md`](../uat/uat-joint-tests.md)
- [`../uat/acceptance-criteria.md`](../uat/acceptance-criteria.md)
- [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md)
- [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md)
