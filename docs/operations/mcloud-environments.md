# MCloud environments — provisioning runbook

> Anchored to TOR ID(s): R2404 (Task 1.5 → Deliverable 1.5, Milestone M1).
> Companion to `docs/pm/tech-infra-requirements.md` (iter 99). Iteration 104.

## 1. Purpose / scope

Define the four environments hosted on MCloud, the provisioning
checklist for each, the responsible parties on both sides
(Supplier DevOps + MCloud operator), and the validation evidence
required before the Acceptance Protocol row "Deliverable 1.5 / R2404"
is signed.

In scope: **dev**, **test**, **training**, **prod** tenancies. Out of
scope: pre-merge ephemeral environments (handled by the supplier CI),
disaster-recovery secondary tenancy (tracked under R2459 / DR plan).

## 2. Audience / stakeholders

- Supplier DevOps lead (provisioning automation, Helm chart owner).
- Supplier security officer (secrets, mTLS certs, audit hooks).
- CNAS MCloud operator (tenancy quotas, network, identity).
- CNAS Service Owner (sign-off for Acceptance Protocol).
- Acceptance committee for M1 / Deliverable 1.5.

## 3. Environment definitions

| Environment | Purpose | Data | Persistence | Notes |
|---|---|---|---|---|
| **dev** | Supplier developer integration. Daily redeploys. | Synthetic. | Ephemeral. Reset on demand. | Open MPass test IdP, no real PII. |
| **test** | Internal QA + automated integration tests. | Synthetic + sanitised samples. | Reset per release. | Mock MGov adapters by default. |
| **training** | UTD-002 / UTD-007-009 user training. | Sanitised fixtures. | Reset per training cohort. | Mirrors prod UI; back-ends mock. |
| **prod** | Live system of record. | Real PII (classification ≥ Restricted). | Backed up per `BackupPolicy` registry. | Real MGov endpoints; MIG-009 residency. |

Each environment occupies its own MCloud project / Kubernetes namespace
pair with its own Vault realm, MinIO bucket prefix, and Postgres
database. **No cross-environment secret reuse.**

## 4. Provisioning checklist (per environment)

### 4.1 Compute

- [ ] Kubernetes namespace created with quota matching
      `docs/pm/tech-infra-requirements.md` §"Compute".
- [ ] API Deployment replicas configured (dev=1, test=2, training=2, prod≥3).
- [ ] Web Deployment replicas (dev=1, test=2, training=2, prod≥3).
- [ ] HPA enabled per `deploy/helm/values-<env>.yaml`.
- [ ] PodSecurity admission `restricted` profile applied.

### 4.2 Storage

- [ ] Postgres instance provisioned (managed service preferred).
- [ ] Read replica for reporting (R2175 / PSR 006); see
      `CnasDbContextOptions.ReadReplicaConnectionString`.
- [ ] MinIO bucket prefix reserved (`cnas-ps-<env>-…`).
- [ ] Backup target reachable per `BackupPolicy.TargetKind` (R2307).

### 4.3 Network

- [ ] Ingress with TLS 1.2+ + HSTS (R2258).
- [ ] Egress allow-list for MGov + external IS endpoints (per
      `docs/integration/technical-integration-specs.md`).
- [ ] WAF + rate-limit rules per R0034.
- [ ] mTLS client certs issued for MConnect (R2254 — gated on MEGA cert).

### 4.4 Secrets + identity

- [ ] Vault realm bootstrapped; `VaultSecretsProvider` configured.
- [ ] Field-encryption KEK loaded (R2281, `AesFieldEncryptor`).
- [ ] MPass client credentials installed (test IdP for dev/test/training,
      production IdP for prod; SAML+X.509 gated on MEGA cert).
- [ ] Service-account least-privilege per R2252.

### 4.5 Observability

- [ ] OTLP exporter pointed at the env-specific collector.
- [ ] Prometheus + Grafana dashboards loaded (R2182 partial).
- [ ] Health endpoints `/health`, `/health/live`, `/health/ready`
      probed by ingress (R2260).

### 4.6 Data residency

- [ ] MIG-009 attestation: data never leaves CNAS-controlled MCloud
      tenancy. Documented in `docs/contract/data-ownership-nda-dpa.md`.

## 5. Validation steps (acceptance evidence)

1. `kubectl get pods -n cnas-ps-<env>` — all Deployments `Available=True`.
2. `curl https://<env>.cnas.gov.md/health` returns `200`.
3. `curl https://<env>.cnas.gov.md/health/ready` returns `200`.
4. `dotnet test Cnas.Ps.slnx` from the deployer host passes against the
   `<env>` connection strings.
5. `perf/cnas-baseline.js` smoke run green (R2170).
6. Audit chain check: `IAuditChainVerifier.VerifyAsync` returns
   `IsValid=true`.
7. Backup smoke: trigger `BackupPolicy=DB_DAILY_FULL` once → verify
   `BackupRun.Status=Succeeded` row exists.
8. Restore smoke (training + prod only): run `docs/dr/dr-drill-runbook.md`
   phases 1-4 against a throw-away DB clone.

## 6. Responsible parties

| Domain | Responsible | Accountable | Consulted |
|---|---|---|---|
| Kubernetes namespace + quota | Supplier DevOps | CNAS MCloud operator | Supplier Architect |
| Postgres / MinIO / Vault | Supplier DevOps | CNAS MCloud operator | Supplier Security Officer |
| Network + WAF | CNAS MCloud operator | CNAS Service Owner | Supplier DevOps |
| Identity + MPass wiring | Supplier Security Officer | CNAS Service Owner | MEGA |
| Backups + DR | Supplier DevOps | CNAS Service Owner | CNAS MCloud operator |
| Acceptance sign-off | CNAS Service Owner | Acceptance Committee | Supplier PM |

## 7. Status / open gaps + references

- Concrete CPU/memory/storage quotas — pending MCloud capacity sizing
  (see `docs/pm/tech-infra-requirements.md` §"Open gaps").
- mTLS certs for MConnect — pending MEGA issuance (R2254).
- Production tenancy provisioning — gated on signed Acceptance
  Protocol row "Deliverable 1.5 / R2404".
- DR secondary tenancy — tracked under R2459 / `docs/bcp-drp-backup-plan.md`.

References:

- TOR §Task 1.5 / §Deliverable 1.5.
- `docs/pm/tech-infra-requirements.md` (iter 99).
- `deploy/helm/` — Helm chart with `values-<env>.yaml` overlays.
- `docs/bcp-drp-backup-plan.md`, `docs/dr/dr-drill-runbook.md`.
- `docs/contract/data-ownership-nda-dpa.md` (MIG-009 residency).
