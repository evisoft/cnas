# Interop acceptance protocol — bilateral template

> Anchored to TOR ID(s): R2425 (Deliverable 3.3, Milestone M3). Iteration 102.
> Bilateral artefact — companion to
> [`technical-integration-specs.md`](technical-integration-specs.md)
> (R2423, iter 100) and [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md).

## 1. Purpose / scope

Template that the supplier and each integration partner fill in to
formally accept the integration between SI „Protecția Socială" and the
partner system. Scope = each MGov MSuite service (MPass / MSign /
MNotify / MConnect / MPay / MCloud) and each typed external IS facade
(RSP, RSUD, SFS, SiSfs, SIVE, SIA-AS, SIA-ISS, SIDDCM, PCCM, ECMnd,
EESSI, FMS). One filled instance of this template per integration
partner per acceptance event.

## 2. Audience / stakeholders

Supplier integration lead, supplier security lead, CNAS integration
owner, CNAS architecture-review board, integration partner technical
contact, integration partner legal/data-protection contact.

## 3. Content + procedure

### 3.1 Procedure

1. The supplier prepares a filled instance of this template per partner.
2. The supplier runs the test plan against the staging integration of the partner system.
3. The supplier records test outcomes + evidence (logs, screenshots, audit-trail extracts) in the appendix.
4. CNAS verifies the evidence against the per-touchpoint specification in [`technical-integration-specs.md`](technical-integration-specs.md).
5. All three sides (supplier / CNAS / partner) sign the protocol.
6. Production integration is enabled.

### 3.2 Fillable template

```markdown
# Interop acceptance protocol — <!-- placeholder: PARTNER_NAME -->

**Integration partner:** <!-- placeholder: legal name + technical owner -->
**Date of acceptance:** <!-- placeholder: YYYY-MM-DD -->
**Spec reference:** docs/integration/technical-integration-specs.md §<!-- placeholder: section -->
**Gap reference:** docs/EGOV-INTEGRATION-GAP.md §<!-- placeholder: section -->

## A. Scope

| Property | Value |
|---|---|
| Service / facade | <!-- placeholder: MPass / MSign / MNotify / MConnect / MPay / MCloud / RSP / RSUD / … -->
| Abstraction (interface) | <!-- placeholder: IM*Client / external facade -->
| Adapter path | <!-- placeholder: src/Cnas.Ps.Infrastructure/MGov/*.cs or External/*.cs -->
| Direction | <!-- placeholder: inbound / outbound / bi-directional -->
| Auth | <!-- placeholder: SAML 2.0 / mTLS X.509 / OAuth2 / WS-Security -->
| Environment under test | <!-- placeholder: integration / pre-prod -->

## B. Test execution log

| # | Test case | Expected | Actual | Pass / Fail | Evidence ref |
|---|---|---|---|---|---|
| 1 | Connectivity (TLS handshake + certificate validation) | 200 / connection established | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 2 | Authentication round-trip | Valid principal / signed token | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 3 | Happy-path request / response | Response matches schema | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 4 | Schema validation (XSD / JSON Schema) | All required fields present | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 5 | Error handling (4xx / 5xx) | Returned error mapped to internal `ErrorCodes` | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 6 | Idempotency (replay safety) | Second submission returns same external txn id | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 7 | Audit-trail emission | `AuditEntry` row written; chain verified | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 8 | Performance — single request latency (p95) | ≤ partner SLA | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 9 | Performance — sustained throughput | ≤ partner contracted RPS | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 10 | Security — credential rotation drill | Old credential rejected; new accepted | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| 11 | Security — outage / circuit-breaker behaviour | Breaker opens; degraded mode logged | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |

## C. Data-protection checklist

| Control | Status | Evidence |
|---|---|---|
| PII fields encrypted in transit | <!-- placeholder --> | <!-- placeholder --> |
| Only minimum-necessary data exchanged | <!-- placeholder --> | <!-- placeholder --> |
| Audit-log entries redacted per `IPiiRedactor` | <!-- placeholder --> | <!-- placeholder --> |
| DPA / NDA in force with partner | <!-- placeholder --> | <!-- placeholder --> |

## D. Outstanding items

<!-- placeholder: any item not closed at sign-off, with owner + due date -->

## E. Sign-off matrix

| Role | Name | Organisation | Signature | Date |
|---|---|---|---|---|
| Supplier integration lead | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| Supplier security lead | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| CNAS integration owner | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| CNAS architecture-review board chair | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| Partner technical contact | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
| Partner data-protection contact | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |
```

### 3.3 Test-case definitions

| Test case | Definition |
|---|---|
| Connectivity | TLS handshake against the partner endpoint; server certificate chain validates against the trust store; client certificate (if mTLS) accepted. |
| Authentication round-trip | A test principal logs in via the partner mechanism; the supplier receives a valid token / SAML assertion / signed envelope; signature verifies. |
| Happy-path | One representative request per direction; payload conforms to the partner schema. |
| Schema validation | Generated stub payloads validate against the partner's XSD or JSON Schema. |
| Error handling | Each documented partner-side error code maps to a stable internal `ErrorCodes.*` value; the application surfaces the mapped error to callers. |
| Idempotency | The same logical request, replayed, produces the same external transaction id (where supported by the partner). |
| Audit-trail emission | Every outbound + inbound call writes an `AuditEntry` whose payload is PII-redacted; `AuditChainIntegrityCheck` is green afterwards. |
| Performance | Single-request latency + sustained throughput against the partner's documented SLA. |
| Credential rotation | Old certificate / token revoked mid-test; new credentials accepted by the partner without service interruption. |
| Outage behaviour | Partner is taken offline (or simulated); circuit breaker opens; user-facing experience matches the documented degraded mode. |

## 4. Acceptance criteria

- One filled instance of this template per integration partner before production cut-over.
- All test cases in §B return Pass, or each Fail has a recorded outstanding item with a due date and risk owner.
- Sign-off matrix complete for every named role.
- Audit-trail emission verified by an `AuditChainIntegrityCheck` run after the test window.

## 5. Implementation map

| Surface | Path |
|---|---|
| Per-touchpoint specification | [`technical-integration-specs.md`](technical-integration-specs.md) |
| Gap audit | [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md) |
| MGov clients | `src/Cnas.Ps.Infrastructure/MGov/IM*Client.cs` + adapters |
| External IS facades | `src/Cnas.Ps.Application/External/*.cs` |
| Audit chain | `tests/Cnas.Ps.Infrastructure.Tests/Auditing/AuditChainIntegrityCheck*` |

## 6. Status / open gaps

- Per-partner pre-prod environments — owned by CNAS + MGov; provisioning is a procurement task.
- MPass SAML refactor + MSign SOAP refactor + MPay SOAP refactor — pending per `EGOV-INTEGRATION-GAP.md`; acceptance against MGov pre-prod must wait until those refactors land.
- Partner-side schemas (XSD / JSON Schema) not all archived in `docs/integration/` — TODO follow-up.

## 7. References

- TOR §Deliverable 3.3
- TODO.md row R2425
- [`technical-integration-specs.md`](technical-integration-specs.md) (R2423)
- [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md)
- [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)
