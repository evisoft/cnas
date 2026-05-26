# DevOps overview

> Top-level DevOps index for SI „Protecția Socială". Aggregates build, deploy,
> operate, observe, recover. Companion to [`operations.md`](operations.md) —
> this document orients, `operations.md` is the runbook.

Audience: SRE, DevOps engineer, technical administrator (STISC),
release manager. Operator instructions reference TOR codes (MR / PSR / SEC /
ARH) where the SLO comes from the contract.

## Lifecycle stages

```
┌────────┐   ┌──────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐
│ Source │ → │ Build │ → │  CI/CD  │ → │  Stage  │ → │  Prod   │ → │ Observe │
└────────┘   └──────┘   └─────────┘   └─────────┘   └─────────┘   └─────────┘
                                                                       │
                                                              ┌────────▼────────┐
                                                              │ Backup & DR     │
                                                              └─────────────────┘
```

Each arrow has a gate. Source → Build: pre-commit hooks. Build → CI/CD:
green pipeline. CI/CD → Stage: auto on `main`. Stage → Prod: manual
approval + smoke E2E. Prod → Observe: OTLP + Serilog → MLog mirror.

## 1. Source control & branching

- Single repo, default branch `main`. Production tracks `main`.
- Branch protection (R0008) — required checks: `format-check`, `build`,
  `test`, `coverage-gate`, `helm-lint`, `codeql`. ≥1 review. Signed
  commits required (currently a GitHub repository-settings TODO — see
  [`operations.md`](operations.md) §"GitHub repository settings").
- Pre-commit hooks via Husky.Net auto-installed on first `dotnet restore`
  (`Directory.Build.props` HuskyInstall target). Three gates:
  1. `dotnet format --verify-no-changes`
  2. `dotnet build -p:TreatWarningsAsErrors=true`
  3. fast unit tests (Core + Application + Architecture only)
  Skip with `HUSKY=0 git commit` only if CI is your safety net.

## 2. Build

- **.NET 10 SDK** on PATH. Solution file: `Cnas.Ps.slnx`.
- Centralised build config: [`Directory.Build.props`](../Directory.Build.props),
  versions in [`Directory.Packages.props`](../Directory.Packages.props).
  `TreatWarningsAsErrors=true` is global. CS1591 enforced (XML docs on
  every public surface — CLAUDE.md RULE 2).
- Containers — two multi-stage non-root images:
  - [`ops/Dockerfile.api`](../ops/Dockerfile.api) — ASP.NET Core REST surface.
  - [`ops/Dockerfile.web`](../ops/Dockerfile.web) — Nginx + Blazor WASM
    static bundle. Nginx config: [`ops/nginx-blazor.conf`](../ops/nginx-blazor.conf).
- Helm chart: [`ops/k8s/cnas-ps/`](../ops/k8s/cnas-ps/README.md) — the only
  supported production deployment surface.

## 3. CI/CD pipelines

GitHub Actions, files in `.github/workflows/`:

| Workflow | File | Trigger | Stages |
|---|---|---|---|
| Continuous integration | `ci.yml` | PR + push to `main` | restore → format-check → build (TWaE) → test+coverage → coverage gate → SAST (CodeQL parallel) → helm-lint |
| Release / image build | `release.yml` | push to `main` | builds `Dockerfile.api` + `Dockerfile.web`, tags by commit SHA, pushes to `ghcr.io/evisoft/cnas-ps-{api,web}` |
| Continuous deployment | `cd.yml` | auto on `main` push, manual to prod | `deploy-staging` → `e2e-staging-gate` → `deploy-production` (manual approval gate) |
| CodeQL SAST | `codeql.yml` | schedule + PR | static analysis. Critical/High blocks merge |
| Performance smoke | `perf-smoke.yml` | nightly + manual | k6 scenarios from `perf/k6/` against staging |

Coverage threshold is **80 %** (`COVERAGE_THRESHOLD` env in `ci.yml`);
ratchet target for UAT-005 is **90 %**. Coverlet exclusions live in
[`coverlet.runsettings`](../coverlet.runsettings).

The CD job protocol — full table in [`operations.md`](operations.md)
§"Continuous-deployment pipeline (R0006)" — uses `helm upgrade --atomic`
plus `helm rollback` on `/health` failure for both staging and production.

## 4. Environments

| Environment | Cluster | Namespace | Postgres | Connect | Source of truth |
|---|---|---|---|---|---|
| **Local dev** | docker-compose | n/a | local PG 16 + PgBouncer | direct | `ops/docker-compose.yml` |
| **CI** | GitHub Actions runners | n/a | EF Core InMemory + Testcontainers | n/a | `.github/workflows/ci.yml` |
| **Staging** | MCloud | `cnas-ps` | Patroni-managed PG 16 | `STAGING_KUBECONFIG` secret | `ops/k8s/cnas-ps/values.staging.yaml` |
| **Production** | MCloud | `cnas-ps` | Patroni-managed PG 16 + streaming replica | `PRODUCTION_KUBECONFIG` secret | `ops/k8s/cnas-ps/values.production.yaml` |

Environment-aware behaviours:

- `Cnas:Captcha:Turnstile:BypassForTesting` — `true` in CI and local;
  `false` in staging and production (live Cloudflare verifier).
- `Cnas:SkipMigrations` — `true` in sandbox runs that don't have a
  database; never set in staging or production.
- `Postgres:Pool:UsePgBouncer` — `true` everywhere PgBouncer is in the
  loop. Set `false` only when debugging Npgsql directly against
  Postgres.
- `ConnectionStrings:PostgresReadReplica` — present in staging and
  production; absent in dev and CI (falls back to primary with a
  `WARNING` log on category `Cnas.Ps.Infrastructure.ReadReplica`).

## 5. Secrets

Secrets never live in source control. Loading order:

1. `Cnas:Secrets:Vault` — HashiCorp Vault (production / staging).
2. Kubernetes `Secret` objects mounted as env vars (Helm value overrides).
3. User Secrets (Development only — `dotnet user-secrets`).
4. `.env` files (local docker-compose only — gitignored).

Inventory and rotation rules: [`operations.md`](operations.md)
§"Secrets matrix" plus the chart-level matrix in
[`ops/k8s/cnas-ps/README.md`](../ops/k8s/cnas-ps/README.md) §"Secrets".
Cardinal secrets:

- `Cnas:FieldEncryption:KeyBase64` — AES-256-GCM master key. **Never
  rotate without a re-encryption window.**
- `Cnas:FieldHashing:SaltKeyBase64` — HMAC-SHA256 salt for identifier
  hash shadows. Rotating invalidates every equality lookup index.
- `Cnas:MGov:MPassSaml:SpCertificate{Pfx,Password}` — MEGA-issued
  X.509 cert (one staging, one production).
- `Cnas:MGov:Mtls:*` — per-client X.509 certs (MNotify et al.).
- `Cnas:Captcha:Turnstile:SecretKey` — Cloudflare Turnstile.
- Postgres + MinIO + Vault root creds.

The chart binds the AES master key to a single named Secret so kubelet
restarts cannot grab a stale envelope.

## 6. Observability

Three pillars, single correlation id:

| Pillar | Stack | Endpoint | Config |
|---|---|---|---|
| Traces | OpenTelemetry → OTLP/gRPC collector | `Cnas:Observability:OtlpEndpoint` | `ObservabilityOptions` |
| Metrics | OpenTelemetry → OTLP/gRPC + Prometheus scrape on `/metrics` | same | same |
| Logs | Serilog JSON → stdout (kubelet harvests to MLog mirror) | n/a | `appsettings.json` `Serilog` block |

Every request carries `correlation_id` (W3C `traceparent` if present,
fresh GUID otherwise) — surfaced as a response header and pushed into
every Serilog log line via `FromLogContext`. EF Core spans intentionally
turn off `SetDbStatementForText` so PII in SQL literals never leaves
the process (SEC 057).

Metric names: `CnasMeter.*` (e.g. `FullTextSearchExecuted`,
`ExaminationVerdictsRecorded`). Trace activity sources:
`Cnas.Ps.Api`, `Cnas.Ps.Application`, `Cnas.Ps.Infrastructure`.

MLog mirror — every audit log row hits Postgres first (`AuditLog`
table, append-only) and is fan-out-mirrored to MLog via `IMLogClient`
through Polly retry + circuit breaker. MLog outage does not block the
caller — failures land in `FailedJob` and are replayed by the
admin-replay endpoint.

## 7. Health checks

| Endpoint | Purpose | Kubernetes probe |
|---|---|---|
| `GET /health/live` | Process is alive. Always 200 unless the host is dead. | Liveness |
| `GET /health/ready` | Every dependency check (`db.postgres`, `storage.minio`, `mgov.*`, `workflow.operaton`) returns `Healthy`. | Readiness |
| `GET /health` | Alias of `/health/ready`. Preserved for older monitoring. | n/a |

**Never** point liveness at `/health/ready` — an MGov outage would
loop-restart pods. Details: [`operations.md`](operations.md)
§"Health endpoints".

## 8. Database

- **Postgres 16** primary + streaming-replication read replica.
- **PgBouncer** in `transaction` pooling mode in front of the primary.
- **Patroni** for leader election (production).
- Per-pod Npgsql cap `MaxPoolSize=2000` (TOR PSR 003 — 2000 concurrent
  users). PgBouncer `default_pool_size=50` real backends behind it.
- EF Core migrations apply at API startup. Skip with
  `Cnas:SkipMigrations=true` (test / sandbox only).
- Read traffic for reporting and registry search routes through the
  replica via `IReadOnlyCnasDbContext` (R0026). Each new service
  flipped requires a read-your-own-writes audit because the InMemory
  test fixture is synchronous.

Full sizing math, transaction-pooling consequences, and the topology
diagrams: [`operations.md`](operations.md) §"Database connection
pooling (R0025)" + §"Read-replica routing (R0026)".

## 9. Object storage

MinIO (S3-compatible). Bucket layout from `MinioOptions`:

- `applications` — citizen-uploaded attachments. Magic-byte validated
  on upload (SEC 010). `MaxFileSizeBytes` default 25 MiB.
- `documents` — generated DOCX / PDF artefacts.
- `archive` — long-term immutable copies, hash-keyed, optionally
  marked via `IFileImmutabilityMarker` (bucket-level S3 Object Lock
  is the deployment-time complement).

## 10. Background jobs

Quartz.NET — `QuartzComposition`. Each job runs through
`FailedJobListener`; failures persist as `FailedJob` rows and are
replayable through `AdminController` (`POST /api/admin/failed-jobs/{id}/replay`).

| Job | Cadence | What it does |
|---|---|---|
| `DossierSlaMonitorJob` | every 15 min | Flags overdue `WorkflowTask` rows + notifies assignee |
| `MPayDispatcherJob` | every 5 min | Drains approved-but-not-yet-paid queue |
| `MConnectSyncJob` | daily 03:00 UTC | Refreshes stale `InsuredPerson` rows from RSP |
| `MakerCheckerExpirySweeper` | every 5 min | Flips Pending → Expired on stale 4-eyes admin actions |

See [`features/background-jobs.md`](features/background-jobs.md).

## 11. Backup and disaster recovery

- Continuous WAL archiving + nightly base backup (Patroni + pgBackRest in
  prod). Retention: 14 daily / 4 weekly / 12 monthly.
- MinIO bucket-replication to a second cluster (DR site) with versioning
  + Object Lock retention.
- RPO target: ≤ 15 min (MR 008). RTO target: ≤ 4 h (MR 010).
- Backup integrity verification: nightly `BackupIntegrityCheck` job +
  weekly restore drill into a sandboxed namespace.

Procedures + restore drills: [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md)
and [`recovery-procedures.md`](recovery-procedures.md).
DR site / failover playbook: [`dr/`](dr/).
Architecture decision records: [`architecture/togaf-adm-artefacts.md`](architecture/togaf-adm-artefacts.md).

## 12. Security operations

- WAF / Cloudflare Turnstile in front of `/api/public/*` (anonymous).
- Rate limiter (in-process) — `Anonymous` 5/60 s, `Authenticated`
  200/60 s, `Callback` 60/60 s, `Upload` 10/60 s, global 500
  concurrent / 1000 queued.
- TLS terminated at the ingress; mTLS for outbound MGov clients that
  require it (MNotify) via `Cnas:MGov:Mtls:*`.
- Outbound IP allow-list maintained at the egress gateway for each
  MGov endpoint — production never reaches public internet for
  registry traffic.
- Audit trail: every privileged action writes an `AuditLog` row before
  acknowledging. SEC 038–043.
- 4-eyes maker-checker on sensitive admin actions (`PendingAdminAction`).
- Threat model: [`../cnas-threat-model.md`](../cnas-threat-model.md).
- Security review: [`../SECURITY-REVIEW.md`](../SECURITY-REVIEW.md).
- Best-practices delta: [`../security_best_practices_report.md`](../security_best_practices_report.md).
- Ownership map after handover: [`handover/security-ownership-map.md`](handover/security-ownership-map.md).

## 13. Performance

- SLO 2000 concurrent users (PSR 003). p95 read ≤ 500 ms, p95 write
  ≤ 1.5 s (PSR 002).
- k6 scenarios in [`../perf/k6/`](../perf/k6/). Nightly perf-smoke runs
  against staging; full load test runs are manual on the staging
  cluster.
- Performance KPIs: [`performance-kpis.md`](performance-kpis.md).
- Tuning notes: [`performance.md`](performance.md),
  [`performance-ops.md`](performance-ops.md).

## 14. Migration & cutover

- Data migration plan: [`migration/`](migration/).
- Go-live strategy: [`go-live-strategy.md`](go-live-strategy.md).
- Production deployment: [`production-deployment.md`](production-deployment.md).
- Stabilisation plan after go-live: [`stabilization/`](stabilization/).
- Post-implementation review: [`pm/sdd-iterative.md`](pm/sdd-iterative.md).

## 15. Support & handover

- Support model + tiers + SLA: [`operations/support-model.md`](operations/support-model.md).
- Operational guides index: [`operations/operational-guides-index.md`](operations/operational-guides-index.md).
- Monthly reports: [`operations/monthly-support-report-template.md`](operations/monthly-support-report-template.md), [`operations/monthly-error-fix-report-template.md`](operations/monthly-error-fix-report-template.md).
- Source-code handover: [`handover/source-code-handover.md`](handover/source-code-handover.md).
- Contract-end procedures: [`handover/contract-end-procedures.md`](handover/contract-end-procedures.md).

## Runbook quick links

| You need to… | Open |
|---|---|
| Deploy or upgrade the stack | [`ops/k8s/cnas-ps/README.md`](../ops/k8s/cnas-ps/README.md) |
| Triage a `/health/ready` 503 | [`operations.md`](operations.md) §"Troubleshooting" |
| Replay a failed background job | `POST /api/admin/failed-jobs/{id}/replay` (admin policy) |
| Rotate a Vault-backed secret | [`operations.md`](operations.md) §"Secrets matrix" |
| Restore a database backup | [`recovery-procedures.md`](recovery-procedures.md) |
| Fail over to the DR site | [`dr/`](dr/) + [`bcp-drp-backup-plan.md`](bcp-drp-backup-plan.md) |
| Audit who accessed a sensitive field | `GET /api/audit-explorer` (CnasAdmin) |
| Check end-to-end response times | k6 dashboard / OTLP metrics |
| Onboard a new SRE | [`roles/administrator-tehnic.md`](roles/administrator-tehnic.md) |
