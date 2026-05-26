# Technical Infrastructure Requirements

> Anchored to TOR ID(s): R2403 (Task 1.4, Deliverable 1.4). Capacity figures
> anchored to TOR PSR 002, PSR 003, PSR 009. Targets fed by
> [`docs/performance-kpis.md`](../performance-kpis.md) (R2176).

## 1. Purpose

Specify the compute, storage, network, observability, and backup substrate
required for SI „Protecția Socială" across Dev, Test/Training, Staging, and
Production environments. Used by the Beneficiary and MCloud operator to
provision MCloud tenancies (R2404).

## 2. Scope

Covers all environments. Out of scope: business logic capacity model and
load profiles — see PSR sizing in `docs/performance-kpis.md`.

## 3. Content / Sections

### 3.1 Compute

- Container runtime: Kubernetes (assumed MCloud-managed; Helm chart shipped
  under `deploy/helm/`). Pod replica defaults: API 3 (anti-affinity zone),
  Web 3, workers 2.
- CPU floor per pod: 1 vCPU request, 2 vCPU limit (API/Web). Worker pods:
  2 vCPU request, 4 vCPU limit for ETL.
- Memory floor: 1 GiB request / 2 GiB limit (API/Web); 2 GiB / 4 GiB workers.
- Horizontal autoscaling: target 65% CPU. Vertical scaling reserved for
  Postgres and PgBouncer nodes.
- .NET runtime: ASP.NET Core (target framework pinned in
  `Directory.Build.props`).

### 3.2 Storage

- Primary database: PostgreSQL 16 with a streaming read-replica. Connection
  routing via PgBouncer (transaction pooling). Pool sizing:
  `src/Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs`.
  Read-side context: `IReadOnlyCnasDbContext` (iter 68/84).
- Blob store: S3-compatible object storage for attachments (`AttachmentRecord`
  aggregate). Bucket-per-environment. Server-side encryption required.
- NFS / file share: notification templates, document templates, batch landing
  zone for Annex-4 offline files.
- Backup target: dedicated S3 bucket per environment, registered through
  `IBackupTarget` + `BackupPolicy` (iter 90). See
  [`docs/bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md).

### 3.3 Networking

- TLS 1.2+ enforced on every public ingress. Internal mesh TLS optional but
  recommended.
- MConnect / MGov integration via the MGov gateway — outbound only. See
  `src/Cnas.Ps.Application/External/` for client facades and
  [`docs/EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md) for status.
- Public ingress only for the Personal Account portal and Interop APIs
  (`InteropController`, `OfflineBatchController`). Back-office is restricted
  to the government VPN.
- Egress allow-list per environment (MGov endpoints, S3, SMTP).

### 3.4 Observability

- OpenTelemetry collector (sidecar or DaemonSet). Application emits metrics
  through `CnasMeter` (see [`docs/performance-ops.md`](../performance-ops.md)).
- Metrics scraping endpoint exposed at `/metrics` on each pod.
- Centralised log sink (structured JSON, key-value).
- Trace propagation: W3C `traceparent`; correlation ID middleware enforced at
  the API edge.
- Audit chain (R0194): tamper-evident chained audit records stored alongside
  application data; verification job in
  `src/Cnas.Ps.Infrastructure/Audit/`.

### 3.5 Backup target

- Each environment owns a dedicated S3 bucket registered as an
  `IBackupTarget`. Policies (full / differential / WAL) selected through
  `BackupPolicy` (iter 90).
- Cross-region replication required for production.
- RTO / RPO targets in [`docs/bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md)
  and [`docs/recovery-procedures.md`](../recovery-procedures.md).

### 3.6 Estimated capacity

Anchored to TOR PSR 002 (concurrent users), PSR 003 (throughput), PSR 009
(peak load). Authoritative targets and current measurements live in
[`docs/performance-kpis.md`](../performance-kpis.md). Headline targets:

- Peak concurrent active users: per PSR 002.
- Sustained transaction throughput: per PSR 003.
- Peak surge multiplier: per PSR 009.

Infrastructure sizing for each environment is derived from those targets and
recorded in `deploy/helm/values-*.yaml`.

## 4. Cadence / Lifecycle

Re-baselined at the end of M1 against MCloud-confirmed quotas, then at each
milestone gate when load profile changes (M2 iteration 6, M4 close, M5 start).

## 5. Implementation map

- Helm chart and environment overlays: `deploy/helm/`.
- DB pool sizing: `src/Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs`.
- Read-replica routing: `IReadOnlyCnasDbContext`.
- Backup registry: `src/Cnas.Ps.Core/Domain/BackupPolicy.cs`,
  `BackupTarget`, `BackupRun`, `BackupIntegrityCheck`.
- Observability: `CnasMeter`, OTel exporters wired in API composition root.

## 6. Status

Skeleton complete. Concrete CPU/memory/storage quotas pending MCloud capacity
confirmation. Tracked by TODO R2403 and R2404 (environment provisioning).

## 7. References

- `tor/TOR.md` §3 (PSR/ARH), §5 (data model), §16 (milestones).
- `docs/performance-kpis.md`, `docs/performance-ops.md`, `docs/performance.md`.
- `docs/bcp-drp-backup-plan.md`, `docs/recovery-procedures.md`.
- `docs/operations.md`, `docs/production-deployment.md`.
- `docs/EGOV-INTEGRATION-GAP.md`.
