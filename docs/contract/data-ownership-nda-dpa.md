# Data ownership, NDA and DPA

> Anchored to TOR ID(s): R2104 (LIPR 007, Phase 15). Iteration 102.
> Contract artefact — not code. Companion to
> [`ip-transfer.md`](ip-transfer.md). Cross-references SEC 035 PII
> encryption and SEC 044 audit redaction implementation.

## 1. Purpose / scope

Establishes that all operational data is owned by CNAS, defines the
supplier's confidentiality obligations (NDA), and specifies the data
processing terms (DPA) under which the supplier acts as processor for
CNAS as controller. Scope = every personal datum, business datum, log
entry, backup, and report handled under the contract.

## 2. Audience / stakeholders

CNAS contracting authority, CNAS legal, CNAS Data Protection Officer
(DPO), supplier legal, supplier security lead, supplier engineering
lead, sub-processors named in writing per §3.3.

## 3. Content + procedure

### 3.1 Data ownership

| Statement | Practical effect |
|---|---|
| CNAS retains exclusive ownership of all data processed by the system | The supplier holds no licence to reuse, aggregate, anonymise-and-sell, or train models on CNAS data. |
| Data never leaves CNAS infrastructure | Migration boundary anchored to MIG 009 — see [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md). |
| Backups remain on CNAS-controlled storage | `BackupExecutionJob` writes only to CNAS-designated `IBackupTarget` instances. |
| At contract end, supplier holds no residual copy | Verified by attestation per [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md). |

### 3.2 Non-Disclosure Agreement (NDA)

- **Confidential information**: every datum the supplier accesses by virtue of the contract, including code paths that reveal business rules, integration credentials, audit chain content, and personal data.
- **Duration**: confidentiality obligations survive for at least **5 years** after contract termination, with no expiry for personal data, classified data, or data covered by RM Law 133/2011.
- **Personnel scope**: every supplier employee, contractor, and sub-processor with system access signs an individual NDA before access provisioning. Roster maintained in the supplier's HR system and made available to CNAS on request.
- **Breach reporting**: any actual or suspected breach reported to CNAS within 24 hours.

### 3.3 Data Processing Agreement (DPA)

CNAS = controller. Supplier = processor. RM Law 133/2011 on personal
data protection applies; GDPR is referenced where the parties choose
GDPR-equivalent practices.

| Clause | Term |
|---|---|
| Subject matter | Operation, support and maintenance of SI „Protecția Socială". |
| Categories of data subjects | Citizens of RM (beneficiaries, dependants), CNAS staff, integration counterparts. |
| Categories of personal data | Identity (IDNP, name, DOB), contact details, financial (bank account, payment history), health-related (disability decisions), benefit history. |
| Lawful basis | Legal obligation (RM social-security law) + public-interest task. |
| Sub-processors | Only those listed in Annex A of the DPA; new sub-processor added only with CNAS written approval ≥30 days in advance. |
| Cross-border transfers | None. Data residency = Republic of Moldova. |
| Security measures | See §3.4. |
| Data-subject-rights support | Supplier supports CNAS in responding to access, rectification, erasure, restriction, and portability requests within statutory deadlines. |
| Breach notification to controller | ≤24 hours from supplier's awareness, regardless of severity. |
| Audit rights | CNAS may audit supplier processing once per year, with reasonable notice. |
| Data return + deletion | At contract end, all CNAS data returned per [`ip-transfer.md`](ip-transfer.md) §3.6 and erased from supplier systems with attestation. |

### 3.4 Technical security measures (DPA Annex B)

| Control | Implementation |
|---|---|
| Encryption at rest of sensitive PII fields | SEC 035 — application-level AES-256 via `EncryptedFieldConverter` on `Person.IdnpEncrypted`, `BankAccount.AccountNumberEncrypted`, etc. (placeholder column names — verify in `src/Cnas.Ps.Infrastructure/Persistence/Conversions/`). |
| Audit-log redaction of PII | SEC 044 — `AuditEntry.PayloadJson` produced via `IPiiRedactor`; assertion in `tests/Cnas.Ps.Infrastructure.Tests/Auditing/AuditRedactionTests.cs`. |
| Encryption in transit | TLS 1.2+ across all internal + external traffic; mTLS for inter-service per ARH 007 (see `ClientCertificateHttpHandler`). |
| Access control | ABAC + RBAC via iter-88 entitlement model; least privilege; four-eyes++ on privileged operations (iter 81/94). |
| Audit trail | Tamper-evident audit chain with `AuditChainIntegrityCheck` (iter 95). |
| Backup encryption | Backup payloads encrypted at the storage layer of `S3CompatibleBackupTarget` (verify configuration; see [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)). |

### 3.5 Sub-processor management

A sub-processor list (`Annex A` of the DPA) is maintained as a
contractual annex. Adding or replacing a sub-processor requires a
written amendment + 30-day prior notice to CNAS.

## 4. Acceptance criteria

- Signed NDA on file for every supplier individual with system access (before access provisioning).
- Signed DPA in force concurrently with the main contract.
- SEC 035 PII encryption present and asserted by integration tests.
- SEC 044 audit redaction present and asserted by integration tests.
- Annex A (sub-processor list) is current.
- Annex B (security measures) is current and matches the deployed configuration.

## 5. Implementation map

| Surface | Path |
|---|---|
| PII encryption (SEC 035) | `src/Cnas.Ps.Infrastructure/Persistence/Conversions/EncryptedFieldConverter.cs` (target path) |
| Audit redaction (SEC 044) | `src/Cnas.Ps.Infrastructure/Auditing/PiiRedactor.cs` (target path) |
| ABAC / RBAC | iter-88 entitlement model in `src/Cnas.Ps.Application/Authorization/` |
| Backup + restore | [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md), [`../recovery-procedures.md`](../recovery-procedures.md) |
| Migration boundary (no data egress) | [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md) |
| Contract-end deletion attestation | [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) |

## 6. Status / open gaps

- DPA Annex A (named sub-processor list) — supplier-side TODO; placeholder until bid award.
- Final contract clause text (NDA + DPA) — owned by CNAS legal — TODO R2104.
- SEC 035 + SEC 044 implementation surfaces above are target paths; verify presence in current tree before sign-off.
- CNAS DPO contact + appointment letter — pending, blocks DPA execution.

## 7. References

- TOR §LIPR 007
- TODO.md row R2104
- RM Law 133/2011 on personal data protection
- RM Law 71/2007 on the National Agency for Personal Data Protection
- TOR §SEC 035, §SEC 044
- [`ip-transfer.md`](ip-transfer.md)
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)
- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)
