# Operations runbook

> Companion to [`../README.md`](../README.md) and
> [`ARCHITECTURE.md`](ARCHITECTURE.md). Targets SREs and ops engineers
> deploying or triaging the CNAS „Protecția Socială" stack.

## Deployment

The production deployment surface is the in-tree Helm chart at
[`ops/k8s/cnas-ps/`](../ops/k8s/cnas-ps/README.md). That README is the
canonical reference for `helm install` / `helm upgrade`, values overlays
(staging vs production), the secrets matrix at chart granularity, and the
chart-specific troubleshooting matrix. **Read it before installing.**

For local development the `ops/docker-compose.yml` file boots Postgres,
MinIO, the API, and the WASM front-end behind Nginx — no Helm involved.

CI builds and pushes the container images (`Dockerfile.api`,
`Dockerfile.web`) to `ghcr.io/evisoft/cnas-ps-api` on every push to `main`
([`.github/workflows/release.yml`](../.github/workflows/release.yml)). The
image tag is the commit SHA; the Helm chart refuses to install with an
empty tag.

### Continuous-deployment pipeline (R0006)

[`.github/workflows/cd.yml`](../.github/workflows/cd.yml) closes the gap in
CLAUDE.md Phase 4 stages 8–11. The jobs are:

| Job | Trigger | Target | Rollback |
|---|---|---|---|
| `deploy-staging` | Auto on every push to `main` (or manual dispatch with `target=staging`) | Staging namespace `cnas-ps`, overlay `values.staging.yaml` | `helm upgrade --atomic` (auto on convergence failure) + `helm rollback` on `/health` retry-loop failure |
| `e2e-staging-gate` | Auto after `deploy-staging` succeeds on a `push` event | Runs `Cnas.Ps.E2E.Tests` with `--filter Category=SmokeStaging` against `STAGING_BASE_URL` | None — failure simply blocks the manual production promotion |
| `deploy-production` | Manual `workflow_dispatch` with required `version` input + manual approval gate on the `production` GitHub Environment | Production namespace `cnas-ps`, overlay `values.production.yaml` | `helm upgrade --atomic` (auto) + `helm rollback` on `/health` retry-loop failure |

Operator hand-off points:

- GitHub Environments `staging` and `production` carry the `KUBECONFIG`
  secret (`STAGING_KUBECONFIG` / `PRODUCTION_KUBECONFIG`, base64-encoded) and
  the `REGISTRY_URL` / `STAGING_BASE_URL` / `PRODUCTION_BASE_URL` variables.
  Configure these before the first run.
- `production` is configured with the **required-reviewers** protection rule
  in CNAS ops; the workflow blocks until a human approves the deployment.
- Post-deploy health check: `curl /health` retried for up to 2.5 min (staging)
  / 5 min (production). On failure the job runs `helm rollback`.

## Health endpoints

Three endpoints are mapped by `UseCnasApiPipeline`
([`src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs`](../src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs)):

| Endpoint | Purpose | Success | Failure |
|---|---|---|---|
| `GET /health/live` | Liveness — process is alive and serving HTTP. Excludes every registered check (`Predicate = _ => false`). | `200` always (unless the process is dead). | n/a — should never 503. |
| `GET /health/ready` | Readiness — all dependencies (Postgres, MinIO, workflow engine, MGov platform) are reachable. Filters on the `ready` tag. | `200` when every dependency check returns `Healthy`. | `503` if any check returns `Unhealthy` or `Degraded`. JSON body lists the failing check name and reason. |
| `GET /health` | Legacy alias of `/health/ready` (same predicate). Preserved for older monitoring scrape configs. | identical to `/health/ready` | identical |

All three are exempt from rate limiting so kubelet probe storms and ops
verifications cannot ever knock pods out of rotation through the limiter.

Wire Kubernetes probes against `/health/live` for liveness and
`/health/ready` for readiness. **Never** point liveness at `/health/ready`
— a flapping MGov dependency would loop-restart the whole pod for no
reason.

The dependency check names — they show up as map keys in the JSON body and
make grep'ing logs cheap — are:

```
mgov.msign, mgov.mpay, mgov.mconnect, mgov.mnotify, mgov.mlog,
mgov.mconnect.events, mgov.mdocs, workflow.operaton,
storage.minio, db.postgres
```

## Configuration reference

The .NET configuration providers stack in the standard order
(`appsettings.json` → environment variables → user secrets in Development).
Every option group is bound at startup and validated; missing required
values fail loudly rather than booting in a broken state.

| Section | Bound type | What it controls | Source |
|---|---|---|---|
| `Cnas:Observability` | `ObservabilityOptions` | OTLP/gRPC endpoint, `service.name`, `deployment.environment`, optional console exporter. Empty endpoint = no exporter, SDK still wired. | [`Cnas.Ps.Api/Composition/ObservabilityOptions.cs`](../src/Cnas.Ps.Api/Composition/ObservabilityOptions.cs) |
| `Cnas:RateLimiting` | `RateLimitingOptions` | Four named policies (`Anonymous`, `Callback`, `Upload`, `Authenticated`) plus the global concurrency ceiling. XFF-trust toggle. | [`Cnas.Ps.Api/Composition/RateLimitingOptions.cs`](../src/Cnas.Ps.Api/Composition/RateLimitingOptions.cs) |
| `Cnas:Captcha:Turnstile` | `TurnstileOptions` | Cloudflare Turnstile CAPTCHA verifier (R0035). `SecretKey` (sensitive — from secrets manager) + `SiteKey` (public — surfaced to SPA) + per-call `Timeout` + `BypassForTesting`. Local dev / integration tests set `BypassForTesting=true` to skip Cloudflare; production / staging set it to `false` and load `SecretKey` from Vault / k8s Secret. | [`Cnas.Ps.Infrastructure/Security/TurnstileOptions.cs`](../src/Cnas.Ps.Infrastructure/Security/TurnstileOptions.cs) |
| `Cnas:FieldEncryption` | `FieldEncryptionOptions` | Base64 AES-256 master key. Missing key triggers fail-loud `MissingKeyFieldEncryptor`. | [`Cnas.Ps.Infrastructure/Security/FieldEncryptionOptions.cs`](../src/Cnas.Ps.Infrastructure/Security/FieldEncryptionOptions.cs) |
| `Cnas:FieldHashing` | `FieldHashingOptions` | Base64 HMAC-SHA256 salt key for identifier hash shadows. Missing key registers `MissingSaltHmacHasher`. | [`Cnas.Ps.Infrastructure/Security/FieldHashingOptions.cs`](../src/Cnas.Ps.Infrastructure/Security/FieldHashingOptions.cs) |
| `Cnas:MGov:Resilience` | `MGovResilienceOptions` | Polly retry + circuit-breaker per MGov client. Master switch `Enabled`. | [`Cnas.Ps.Infrastructure/MGov/MGovResilienceOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MGovResilienceOptions.cs) |
| `Cnas:MGov:Mtls` | `MTlsOptions` | Per-client X.509 client-certificate paths and PFX passwords. | [`Cnas.Ps.Infrastructure/MGov/MTlsOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MTlsOptions.cs) |
| `Cnas:MGov:MPassSaml` | `MPassSamlOptions` | SAML issuer URL, SP entity-id, clock skew, attribute map. (Wired by the SAML parser; the live middleware swap is gated.) | [`Cnas.Ps.Infrastructure/MGov/MPassSamlOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MPassSamlOptions.cs) |
| `Cnas:MCabinet` | `MCabinetOptions` | MCabinet base URL + stable `SystemCode` (`CNAS-PS`). Never change `SystemCode` after launch. | [`Cnas.Ps.Infrastructure/MGov/MCabinetOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MCabinetOptions.cs) |
| `Cnas:Secrets:Vault` | `VaultSecretsOptions` | Optional HashiCorp Vault configuration for `ISecretsProvider`. | [`Cnas.Ps.Infrastructure/Secrets/VaultSecretsOptions.cs`](../src/Cnas.Ps.Infrastructure/Secrets/VaultSecretsOptions.cs) |
| `MGov` | `MGovOptions` | Per-service base URLs + MPass OIDC client id / issuer / secret (placeholder until SAML middleware lands). | [`Cnas.Ps.Infrastructure/MGov/MGovOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MGovOptions.cs) |
| `Minio` | `MinioOptions` | Endpoint, access / secret keys, SSL toggle, bucket names, `MaxFileSizeBytes` (default 25 MiB / SEC 010). | [`Cnas.Ps.Infrastructure/Storage/MinioOptions.cs`](../src/Cnas.Ps.Infrastructure/Storage/MinioOptions.cs) |
| `Workflow` | `WorkflowOptions` | Operaton workflow-engine connection settings. | [`Cnas.Ps.Infrastructure/Workflow/WorkflowOptions.cs`](../src/Cnas.Ps.Infrastructure/Workflow/WorkflowOptions.cs) |
| `Sqids` | `SqidOptions` | Alphabet + minimum encoded length. Immutable after launch. | [`Cnas.Ps.Infrastructure/Common/SqidOptions.cs`](../src/Cnas.Ps.Infrastructure/Common/SqidOptions.cs) |
| `ConnectionStrings:Postgres` | string | EF Core / Npgsql connection string. The host should point at PgBouncer (not Postgres) in every non-local environment; see [Database connection pooling (R0025)](#database-connection-pooling-r0025). | [`Cnas.Ps.Api/appsettings.json`](../src/Cnas.Ps.Api/appsettings.json) |
| `Postgres:Pool` | `PostgresPoolOptions` | Per-pod Npgsql pool sizing in front of PgBouncer (R0025). Defaults to `MaxPoolSize=2000` per TOR PSR 003; operators can override individual knobs via env vars (e.g. `Postgres__Pool__MaxPoolSize`). When `UsePgBouncer=true` (the default) prepared-statement auto-prep and server-state reset are disabled — see the type-level remarks. | [`Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs`](../src/Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs) |
| `Cors:AllowedOrigins` | `string[]` | CORS origin allow-list for the WASM front-end. Lock down in production. | `appsettings.json` |
| `Cnas:SkipMigrations` | `bool` | When `true`, the API skips `DbContext.Database.MigrateAsync` at startup — used by tests and sandbox runs. | [`Cnas.Ps.Api/Program.cs`](../src/Cnas.Ps.Api/Program.cs) |

## Database connection pooling (R0025)

The TOR PSR 003 SLO is **2000 concurrent users**. Each in-flight HTTP request
can hold a database connection for its duration, so at peak the API needs
roughly that many *client-side* connections per pod. PostgreSQL cannot
sustain 2000 native backends (each one is an OS process consuming
~10 MB RAM + a PID), so production fronts Postgres with **PgBouncer in
`transaction` pooling mode** — clients see the wire as if it were Postgres,
but PgBouncer multiplexes the 2000 client connections onto a much smaller
pool of real backends.

The two-tier sizing:

| Tier | Knob | Default | Rationale |
|---|---|---|---|
| App (Npgsql) | `Postgres:Pool:MaxPoolSize` | 2000 | Matches PSR 003 — every concurrent user may hold its own client connection. Per pod. |
| App (Npgsql) | `Postgres:Pool:MinPoolSize` | 5 | Idle connections kept warm so the first request post-cold-start skips TLS / SCRAM handshake on the critical path. |
| App (Npgsql) | `Postgres:Pool:CommandTimeout` | 30 s | Same upper bound as every outbound MGov client. Long reporting queries belong in Quartz (R0048), not the request lane. |
| App (Npgsql) | `Postgres:Pool:UsePgBouncer` | `true` | Disables session-state prepared statements + Npgsql's server-reset because PgBouncer transaction-pooling cannot guarantee a single backend across a checkout (see "Consequences" below). Set to `false` when debugging directly against Postgres without PgBouncer in the loop. |
| PgBouncer | `default_pool_size` | 50 | Real Postgres backends per database. Each one steady-state serves ~40 active transactions per second by rotation. |
| PgBouncer | `max_client_conn` | 2500 | Accept budget — slightly above the per-pod app cap so an HPA burst doesn't drop the first 500 connections of a newly-scheduled pod. |
| PgBouncer | `reserve_pool_size` | 10 | Emergency overdraft when `default_pool_size` is exhausted; capped at `reserve_pool_timeout=5s`. |

The math: at 2000 concurrent users → 2000 client connections per pod → ~50
real Postgres backends doing the work after PgBouncer multiplex. A single
Postgres leader can comfortably host several hundred backends total, so
horizontal API scale-out doesn't drag Postgres backend count linearly.

### Consequences of transaction-pooling mode

Transaction-pooling does NOT preserve session state across query boundaries
— the same client connection may be served by a different Postgres backend
on the next checkout. Three Npgsql settings are flipped accordingly by
`AddCnasInfrastructure` when `Postgres:Pool:UsePgBouncer=true`:

- **`Max Auto Prepare = 0`** — prepared statements bind server-side state to
  the specific backend that prepared them. Auto-prepare would silently break
  on the next checkout. We pay the parse cost per execute in exchange.
- **`No Reset On Close = true`** — PgBouncer itself runs `SERVER_RESET_QUERY`
  (`DISCARD ALL`) between transactions; Npgsql's reset would be redundant
  noise on the wire.
- **`EnableTypeLoading(false)`** on the `NpgsqlDataSource` builder — skips
  the pg_catalog type-discovery round-trip that PgBouncer cannot proxy
  reliably in transaction mode.

Custom types (CITEXT, ENUMs we own, JSON shapes) continue to round-trip
correctly because their EF Core mappings live in `CnasDbContext`'s model
configuration — we don't depend on Npgsql's runtime type-discovery for any
domain column.

### Topology — dev (docker-compose)

`ops/docker-compose.yml` boots a `pgbouncer` service in front of `postgres`:

- `pgbouncer:6432` is the address the API connects to
  (`ConnectionStrings__Postgres=Host=pgbouncer;Port=6432;…`).
- `postgres:5432` stays exposed for ad-hoc `psql` introspection but is no
  longer the API's entry point.
- The image is pinned to `edoburu/pgbouncer:1.23.1` (never `latest`) so a
  surprise upstream change cannot alter pool semantics underneath a working
  deployment.
- The PgBouncer password is read from the `POSTGRES_PASSWORD` environment
  variable — never committed.

### Topology — production (Helm)

The in-tree Helm chart at [`ops/k8s/cnas-ps/`](../ops/k8s/cnas-ps/) is the
canonical production wiring. The chart currently deploys Postgres directly;
a follow-up batch (R0038) is wiring a PgBouncer sidecar / sidekick deployment
between the API pods and the Patroni leader so the production topology
matches the dev compose stack 1:1. Until that lands, set
`Postgres__Pool__UsePgBouncer=false` on the API deployment to point Npgsql
directly at the Postgres ClusterIP service — the per-pod `MaxPoolSize=2000`
still applies but prepared statements and the standard server-reset path
remain active.

### Operator knobs

| Variable | When to change |
|---|---|
| `Postgres__Pool__MaxPoolSize` | Override per environment if the pod sizing departs from PSR 003 (e.g. a load-test pod running with a 200-connection cap). |
| `Postgres__Pool__UsePgBouncer` | `false` for local debugging directly against Postgres without PgBouncer. |
| `Postgres__Pool__CommandTimeout` | Raise only after moving the slow query to the background-job lane is not feasible. |

## Read-replica routing (R0026)

TOR PSR 006 / ARH 025 require reporting and Annex 5/6 long-running list queries to land on a **Postgres streaming-replication replica** so the primary backend stays free for write workloads. SI PS exposes this routing through a dedicated `IReadOnlyCnasDbContext` abstraction in [`src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs`](../src/Cnas.Ps.Application/Abstractions/IReadOnlyCnasDbContext.cs); the concrete `CnasReadOnlyDbContext` ([`src/Cnas.Ps.Infrastructure/Persistence/CnasReadOnlyDbContext.cs`](../src/Cnas.Ps.Infrastructure/Persistence/CnasReadOnlyDbContext.cs)) is wired in [`InfrastructureServiceCollectionExtensions.cs`](../src/Cnas.Ps.Infrastructure/InfrastructureServiceCollectionExtensions.cs).

### Connection strings

| Key | What it is | Required in |
|---|---|---|
| `ConnectionStrings:Postgres` | Primary read/write Postgres endpoint. | Every environment. |
| `ConnectionStrings:PostgresReadReplica` | Read-only Postgres streaming-replication replica endpoint. | **Production / staging.** In dev / single-Postgres CI it may be omitted; the wiring transparently falls back to the primary. |

Set the two values to the same connection string in development. In production set the replica to the streaming-replication follower's TCP endpoint (typically a separate Patroni-managed read-only service IP) — the same `Postgres:Pool:*` sizing applies to both connections in this batch (a separate `Postgres:ReplicaPool:*` section can be introduced later if the analytical workload needs a different cap).

### Fallback warning

When `ConnectionStrings:PostgresReadReplica` is unset, [`ReadReplicaConfiguration.ResolveConnectionString`](../src/Cnas.Ps.Infrastructure/Persistence/ReadReplicaConfiguration.cs) emits a `Warning`-level log line with the category `Cnas.Ps.Infrastructure.ReadReplica` saying:

```
ConnectionStrings:PostgresReadReplica is unset; read-only context will route to the primary.
This is acceptable for dev but should NOT be the case in production (TOR PSR 006).
```

The fallback never throws; the host boots successfully so the rest of the system still runs. Production runbooks MUST treat this WARN line as a paging-grade misconfiguration.

### Replica lag semantics

The replica catches up with the primary asynchronously (typical lag: tens to hundreds of milliseconds; longer under maintenance or replication-slot saturation). Services that need **read-your-own-writes** guarantees MUST stay on `ICnasDbContext` (the read/write context bound to the primary). Examples:

- The application-submission flow that re-reads its own row inside the same request — stays on `ICnasDbContext`.
- The maker-checker approve endpoint that loads the pending row, mutates it, and saves — stays on `ICnasDbContext`.
- Annex 6 aggregations triggered by an operator — flow through `IReadOnlyCnasDbContext` since the operator does not expect their own freshly-written row to be visible in the same report run.

### Services consuming the read-only context today

| Service | Source file | Workload type |
|---|---|---|
| `ReportingService` (all Annex 6 / 6b / .../ 6j builders) | [`src/Cnas.Ps.Infrastructure/Services/ReportingService.*.cs`](../src/Cnas.Ps.Infrastructure/Services/) | Aggregations + per-entity exports |
| `DataSearchService` (UC03 / UC12 registry search) | [`src/Cnas.Ps.Infrastructure/Services/UseCaseStubs.cs`](../src/Cnas.Ps.Infrastructure/Services/UseCaseStubs.cs) | Paginated LIKE search across Contributors / InsuredPersons / Applications |

More services can be flipped over time but each flip requires careful audit to ensure no read-your-own-writes expectations break — the test fixture's InMemory store is synchronous so a regression there would not catch the production lag. Candidates for a future batch: `PublicContentService` (anonymous catalog read), `DashboardService` (KPI aggregations), `ContributorService` / `InsuredPersonService` listing endpoints, `AuditService` log queries.

### Drift protection

A reflection-based test ([`tests/Cnas.Ps.Infrastructure.Tests/Persistence/CnasReadOnlyDbContextTests.cs`](../tests/Cnas.Ps.Infrastructure.Tests/Persistence/CnasReadOnlyDbContextTests.cs) → `EveryDbSet_IsMirroredAsIQueryable`) asserts that every `DbSet<T>` on `ICnasDbContext` has a matching `IQueryable<T>` property of the same name on `IReadOnlyCnasDbContext`. A new entity added to the writable contract must also be added to the read-only contract in the same commit, or the build fails.

### R1904 — Long-running report services verified on the read-replica (ARH 025)

R0026 shipped the read-replica seam; R1904 cements it. Every concrete service whose job is to aggregate over significant slices of the database — and therefore MUST run on the streaming-replication follower in production — carries a structural marker that the architecture suite enforces.

**Marker.** [`LongRunningReportServiceAttribute`](../src/Cnas.Ps.Application/Reporting/LongRunningReportServiceAttribute.cs) is a class-level attribute (no properties, `Inherited=false`, `AllowMultiple=false`). Applying it to a service is the developer's explicit declaration that the service is pure-read and that its data access goes through `IReadOnlyCnasDbContext`. Services marked today: `ReportingService` (the canonical Annex 6 / 6b / ... / 6j entry point) and `AccessRightsReportService` (R2274 — its only write is an audit row dispatched via `IAuditService`, not via the writable context).

**Architecture test.** [`ReadReplicaLayeringTests`](../tests/Cnas.Ps.Architecture.Tests/ReadReplicaLayeringTests.cs) ships four ratchets in the arch suite:

| Test | What it pins |
|---|---|
| `LongRunningReportServices_DoNotInjectWritableContext` | Every type marked `[LongRunningReportService]` MUST inject `IReadOnlyCnasDbContext` and MUST NOT inject `ICnasDbContext`. Reflection scans constructor parameters and reports the offending type + parameter name. |
| `ReportingService_IsMarkedAsLongRunningReportService` | Pins the marker on `ReportingService` so it cannot be silently removed. |
| `LongRunningReportServiceMarker_AppliesToClassesOnly` | Pins the attribute's `AttributeUsage` shape (target = class, not inherited, not multiple). |
| `AnyTypeNamedReportService_CarriesMarkerOrIsAllowlisted` | Every concrete `*ReportService` class in `Cnas.Ps.Infrastructure` must either carry the marker OR appear in the `HybridReportServicesAllowlist` with a justification comment. New report services therefore force a deliberate choice. |

**Hybrid services.** Some services that *look* like report services (e.g. `IntegrityCheckService`, `ClassificationCatalogService`) legitimately write rows (findings, snapshots) and therefore inject the writable `ICnasDbContext`. They MUST NOT carry the marker. The allowlist in `ReadReplicaLayeringTests.HybridReportServicesAllowlist` documents these exceptions with comments; the current iteration ships the list empty (all candidate services are either pure-read and marked, or non-`*ReportService` named).

**Metric tag.** [`CnasMeter.ReportingServiceQueryExecuted`](../src/Cnas.Ps.Infrastructure/Observability/CnasMeter.cs) increments once per `BuildDatasetAsync` invocation with the tag `db_context = "read_replica"`. The constant value pins, at telemetry level, the contract the marker promises at compile time. Operators charting `cnas.reporting.query_executed{db_context="read_replica"}` can confirm at runtime that long-running report workloads are landing on the replica.

**Rule for new report services.** A new service that aggregates over significant slices of the data MUST be added to this seam — pure-read with the marker by default; hybrid only with an allowlist entry and a comment explaining why the dual-context pattern is required.

## Secrets matrix

| .NET binding path | Env var (Kubernetes flavour) | What it is | Rotation seam |
|---|---|---|---|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | Npgsql connection string for the application DB user. | Patroni-coordinated password change of the `cnas` role; rolling restart of the API picks up the new connection string. |
| `Cnas:FieldEncryption:Key` | `Cnas__FieldEncryption__Key` | Base64 AES-256 master key (exactly 32 bytes decoded). | **Gradual.** The ciphertext envelope carries a `vN:` version prefix — `AesFieldEncryptor` only recognises `v1` today; a future `v2` would let new writes use the new key while old reads continue to decrypt `v1`. Re-encryption job is then run in the background under a maintenance flag. Until `v2` exists, a key change requires a coordinated re-encryption pass over every encrypted row before the old key is retired. |
| `Cnas:FieldHashing:SaltKey` | `Cnas__FieldHashing__SaltKey` | Base64 HMAC-SHA256 secret used for identifier hash shadows. | **Full rewrite under maintenance window.** The shadow column carries no version (the unique index needs a single hash value per row), so the envelope trick that works for encryption does not apply. Quiesce writes, recompute every shadow column, swap the salt, resume. |
| `Cnas:Storage:Minio:AccessKey` / `Cnas:Storage:Minio:SecretKey` (per Helm secret-injection paths) | `Cnas__Storage__Minio__AccessKey` / `Cnas__Storage__Minio__SecretKey` | MinIO root credentials. The application code itself binds these from `Minio:*` — confirm the environment-variable mapping in the chart matches the runtime binding in [`InfrastructureServiceCollectionExtensions.cs`](../src/Cnas.Ps.Infrastructure/InfrastructureServiceCollectionExtensions.cs) before rotation. [verify in your deployment] | MinIO root-key rotation; restart API pods to re-load. |
| `MGov:MPassClientSecret` | `MGov__MPassClientSecret` | MPass OIDC client secret (placeholder until SAML cuts over). | Re-issue at the MPass portal; staging / production secrets are independent. |
| `Cnas:MGov:Mtls:Certificates:<svc>:Path` and `:Password` for `<svc>` in {`msign`, `mpay`, `mconnect`, `mnotify`, `mlog`, `mdocs`, `mconnect-events`, `mcabinet`} | `Cnas__MGov__Mtls__Certificates__<svc>__Path` / `__Password` | Mounted PFX file paths and decrypt passwords for the per-service client certificates. | Per-service PFX rotation. The path is typically a mounted volume from the chart's `Secret` or `ExternalSecret`. |
| `Cnas:Captcha:Turnstile:SecretKey` | `Cnas__Captcha__Turnstile__SecretKey` | Cloudflare Turnstile server-side secret (R0035 — anonymous-surface abuse prevention on UC01 / UC02). | Re-issue at the Cloudflare dashboard; the new value is picked up the next time the DI singleton is rebuilt (rolling restart). Drain anonymous traffic — or accept short-lived 400/503s — before swapping. |

In production every secret must come from an external store (Vault / k8s
Secrets backed by ExternalSecrets / MCloud KMS) — see
[`ops/k8s/cnas-ps/README.md`](../ops/k8s/cnas-ps/README.md) "Secrets matrix"
for the chart-level binding chain.

## Runbook

Five failure modes and how to triage. The chart-level troubleshooting matrix
(CrashLoopBackOff on missing key, PDB-blocked drain, Patroni quorum, etc.)
lives in [`ops/k8s/cnas-ps/README.md`](../ops/k8s/cnas-ps/README.md);
duplicated here only where ops engineers reach for the application repo
first.

### 1. API pod failing readiness

Symptom: kubelet reports the pod `NotReady`; new traffic is drained off the
service. The pod itself is alive.

Triage:

```bash
kubectl -n <ns> port-forward <api-pod> 8080:8080
curl -sS http://localhost:8080/health/ready | jq .
```

The JSON body lists each dependency check (`db.postgres`, `storage.minio`,
`workflow.operaton`, `mgov.*`) with its individual status and the failure
reason for any non-healthy entries. Triage by the failing tag:

- `db.*` — Postgres unreachable (see runbook 2).
- `storage.*` — MinIO down or the bucket is missing.
- `workflow.*` — Operaton unhealthy or unreachable.
- `mgov.*` — Specific upstream is down; verify in the chart values that the
  client base URL is correct and the mTLS cert is valid.

### 2. PostgreSQL leader unavailable

Symptom: every API pod's `/health/ready` reports `db.postgres` unhealthy.
Patroni-managed cluster, deployed by the chart.

Triage path is fully in the chart README — see "Patroni leader election
stuck" in [`ops/k8s/cnas-ps/README.md`](../ops/k8s/cnas-ps/README.md).
TL;DR: check the Patroni ServiceAccount RBAC on endpoints; re-apply the
chart if the RoleBinding drifted.

### 3. Quartz job stuck in a retry loop

Symptom: the same job (`mpay-dispatcher`, `mconnect-sync`, etc.) keeps
appearing in logs with the same error. The dead-letter queue accumulates.

Triage:

```bash
# Page the DLQ (auth required — CnasTechAdmin policy).
curl -sS https://<api-host>/api/admin/failed-jobs?page=1&pageSize=50 \
  -H "Cookie: cnas.session=..." | jq .

# Replay a single failed job by its Sqid id (idempotent on the job).
curl -X POST https://<api-host>/api/admin/failed-jobs/<sqid>/replay \
  -H "Cookie: cnas.session=..."
```

The endpoints map to `AdminController.ListFailedJobsAsync` and
`ReplayFailedJobAsync`. Both back onto `IFailedJobStore`, which persists
rows in the `FailedJobs` table (`Cnas.Ps.Core.Domain.FailedJob`).

If the same DLQ entry comes straight back after replay the underlying cause
isn't transient — fix the upstream, then drain.

### 4. Rate limiter rejecting legitimate traffic

Symptom: spike of `429 Too Many Requests` in API logs from a known good
caller (citizen portal, internal scheduled job, mobile client).

Triage:

1. Confirm which policy is gating: the OpenAPI tags / controller attributes
   tell you the partition policy (see
   [`RateLimitingPolicies.cs`](../src/Cnas.Ps.Api/Composition/RateLimitingPolicies.cs)).
2. Inspect the bound config:
   ```bash
   kubectl -n <ns> get configmap <release>-cnas-ps-api-config -o yaml \
     | grep -A1 'rateLimiting'
   ```
3. If the policy is IP-partitioned (`Anonymous`, `Callback`), check the
   Ingress is stripping client-supplied `X-Forwarded-For` and only the
   gateway's own value is forwarded. The chain rule is documented on
   `RateLimitingOptions.TrustForwardedHeaders` — when XFF can't be trusted,
   set `Cnas:RateLimiting:TrustForwardedHeaders=false` and bucket on the
   connection IP instead.
4. To widen a policy on the fly bump `Cnas:RateLimiting:<Policy>:PermitLimit`
   in the ConfigMap and roll the API pods — runtime reconfiguration is not
   supported (the limiter SDK builds partition state at startup).

### 5. MGov client circuit-breaker open

Symptom: requests against a specific MGov upstream (e.g. MSign) immediately
return failures without an outbound HTTP attempt; OpenTelemetry traces show
the resilience pipeline rejecting at the breaker stage.

Triage:

1. In Jaeger / Tempo (or whichever OTLP backend is wired), filter spans by
   the `http.client` instrumentation and the upstream host. A wave of
   failures preceding the breaker-open event indicates which upstream is
   actually broken.
2. Inspect the per-client resilience settings:
   `Cnas:MGov:Resilience:Clients:<name>` (one of `msign`, `mpay`, `mconnect`,
   `mnotify`, `mlog`, `mdocs`, `mconnect-events`, `mcabinet`). The defaults
   are documented inline in `MGovResilienceOptions.cs`.
3. If the upstream genuinely needs more headroom (handshake-heavy SAML or
   SOAP rounds), increase the retry count and the timeout — never widen the
   failure-threshold without simultaneously checking whether the upstream is
   actually recovering, because the half-open probe is what closes the
   breaker.
4. The emergency switch is `Cnas:MGov:Resilience:Enabled=false` — this turns
   the whole Polly pipeline into a no-op. Use only as a last resort while
   you're hunting a misconfiguration; production must stay `true`.

## GitHub repository settings (R0008)

These are pure configuration on the repository's GitHub settings page — there
is nothing to ship in code. Apply once on initial setup, and re-verify after
any organisation-wide policy change.

### Branch protection (R0008)

> Iter 143 (2026-05-25): Operator-side checklist confirmation. The runtime
> guarantee for R0008 depends on the GitHub-side toggles below being applied
> exactly as listed; the code-side enforcement (CI pipeline that *would be*
> blocked) is already in place via `.github/workflows/ci.yml`. Tick each row
> below in the GitHub UI and record the date so the next audit can see who
> applied the toggle.
>
> | Toggle                                              | Required value | Applied (date / operator) |
> |-----------------------------------------------------|----------------|---------------------------|
> | Require PR before merging                           | yes            |                           |
> | Required approving reviews                          | ≥ 1            |                           |
> | Dismiss stale approvals on new commits              | yes            |                           |
> | Required status checks (see list below)             | all green      |                           |
> | Require branches to be up to date before merging    | yes            |                           |
> | Require signed commits                              | yes            |                           |
> | Restrict who can push directly to `main`            | nobody         |                           |
> | Allow force pushes                                  | NO             |                           |
> | Allow deletions                                     | NO             |                           |
>
> The runtime guarantee for R0008 is satisfied once every row in this table is
> ticked on the production GitHub repository.

**Branch protection on `main`:**

1. Settings → Branches → Add classic branch protection rule for `main`.
2. Require a pull request before merging — minimum **1 approving review**;
   dismiss stale approvals on new commits; require review from code owners
   if a `CODEOWNERS` file is added later.
3. Require status checks to pass before merging — mark these `ci.yml` jobs
   as required checks once they exist on the default branch:
   - `format-check` (or whichever job runs `dotnet format --verify-no-changes`)
   - `build` (warnings-as-errors)
   - `test` + `coverage-gate`
   - `sast`
   - `helm-lint`
4. Require branches to be up to date before merging.
5. Require **signed commits**.
6. Restrict who can push to matching branches → leave empty so only the merge
   path through PRs is open; nobody can `git push` directly to `main`.
7. Do NOT allow force pushes; do NOT allow deletions.

**Commit signing:**

Contributors enable GPG- or SSH-key signing on their local clone (see GitHub's
"Telling Git about your signing key" docs). The pre-commit hook chain from
R0005 does not enforce signing — the server-side branch protection rule above
is the enforcement point.

**Trunk-based flow:**

Feature branches stay short (≤ ~3 days where possible). Squash-merge to `main`
to keep the history linear; rebase-and-merge is also acceptable. Avoid merge
commits on `main` because they make `git bisect` noisier when tracking
regressions across phases.

## Metrics (R0040 partial close)

Every Infrastructure subsystem emits custom OTel counters and gauges on the
`Cnas.Ps.Subsystems` meter (see `src/Cnas.Ps.Infrastructure/Observability/CnasMeter.cs`).
The meter is registered with the OTLP exporter inside `ApiCompositionRoot.AddCnasObservability`
via the wildcard subscription `AddMeter("Cnas.Ps.*")` plus an explicit
`AddMeter(CnasMeter.MeterName)`. Counters never carry user identifiers, IDNPs,
IP addresses, or token hashes — tags are bounded cardinality only (see
`CnasMeter` XML doc for the policy).

Prometheus / Grafana / Loki deployment infrastructure lives at the Helm /
operator level (out of repo). R0040 remains `[~]` partial until the chart
ships those panels; the application-side metric surface listed here is the
in-repo half.

### Counters

| Name | Tags | Operator signal |
|------|------|-----------------|
| `cnas.audit.enqueued` | — | Throughput of the audit pipeline producer side. Flat vs spiky charts the application workload. |
| `cnas.audit.dropped` | `reason` ∈ {`queue_full`, `flush_failed`, `archive_failed`} | **Page on-call.** Any sustained non-zero rate means audit records are not reaching durable storage. |
| `cnas.audit.flushed` | `batch.size_bucket` ∈ {`1`, `5`, `10`, `50`} | Drainer health. A batch-size distribution skewed to `50` means the producer is outrunning the drainer (backlog forming). |
| `cnas.audit.archived` | — | Primary flush path is degraded. Replay job will retry — but investigate the DB / MLog upstream. |
| `cnas.audit.replay.attempted` | — | Replay job actively processing a backlog. Sustained `>0` means the primary flush is still failing. |
| `cnas.audit.replay.succeeded` | — | Replay backlog draining. |
| `cnas.audit.replay.failed` | — | Replay backlog NOT draining — persistent DB outage. |
| `cnas.audit.chain.verified` | `chain.valid` ∈ {`true`, `false`} | **Page on-call** on any `chain.valid=false` — the audit chain has been tampered (SEC 047). |
| `cnas.jwt.access.issued` | — | Login traffic. |
| `cnas.refresh.issued` | — | New login families. |
| `cnas.refresh.rotated` | — | Refresh-token rotations (per active session ≈ every 15 minutes). |
| `cnas.refresh.reuse_detected` | `family.revoked=true` | **Page on-call.** Refresh-token reuse → either a buggy client or a stolen token (SEC 018). |
| `cnas.refresh.revoked` | — | Logout traffic. |
| `cnas.admin.action.submitted` | — | Maker-checker traffic. |
| `cnas.admin.action.approved` | — | 4-eyes approvals. |
| `cnas.admin.action.rejected` | — | 4-eyes rejections. |
| `cnas.admin.action.expired` | — | TTL-driven sweeps. Sustained `>0` means checkers are missing their SLA. |

### Gauges

| Name | Operator signal |
|------|-----------------|
| `cnas.audit.queue.depth` | Backlog at the drainer input. Should sit ≪ 4096 (the bounded-channel capacity). Climbing trend = primary flush degraded. |
| `cnas.audit.archive.size` | Pending replay files on disk. Should sit at 0; non-zero = primary flush failed at some point, replay job is catching up. |
| `cnas.admin.action.backlog` | Open admin actions awaiting a second-administrator decision (refreshed every 30s via `AdminActionBacklogObserver`). Climbing trend = checkers absent. |

### Suggested Grafana panels

- **Audit pipeline health** — stacked area of `cnas.audit.enqueued` /
  `cnas.audit.flushed` / `cnas.audit.archived` /
  `cnas.audit.dropped{reason}` with `cnas.audit.queue.depth` on a secondary
  axis.
- **Refresh-token security** — single-stat for `cnas.refresh.reuse_detected`
  over 5 min (red if `>0`), time-series of
  `cnas.refresh.issued` / `cnas.refresh.rotated` / `cnas.refresh.revoked` for
  baseline shape.
- **Maker-checker queue** — gauge for `cnas.admin.action.backlog`, stacked
  bar of submitted / approved / rejected / expired.
- **Audit chain integrity** — single-stat for
  `sum(cnas.audit.chain.verified{chain.valid="false"})` (red on `>0`).

## Observability stack (R0040 / R2182)

The `ops/k8s/cnas-ps/values.observability.yaml` overlay layers Prometheus +
Alertmanager + Grafana + Loki + the OpenTelemetry Collector on top of the
base Helm chart so a fresh cluster ships with a complete observability
surface. Apply alongside the environment overlay:

```bash
helm upgrade --install cnas-ps ./ops/k8s/cnas-ps \
  -f ops/k8s/cnas-ps/values.yaml \
  -f ops/k8s/cnas-ps/values.production.yaml \
  -f ops/k8s/cnas-ps/values.observability.yaml
```

Knobs surfaced through the overlay:

| Key | Default | Notes |
|-----|---------|-------|
| `prometheus.server.persistentVolume.size` | `10Gi` | ~30 days of 15-second scrape granularity. |
| `prometheus.server.retention` | `30d` | Aligns with the SI-PS audit-archive retention window. |
| `prometheus.alertmanager.enabled` | `true` | PagerDuty / email / Slack receivers live in the `cnas-alertmanager-config` Secret. |
| `grafana.adminUser` | `admin` | Admin password sourced from `cnas-grafana-admin` Secret. |
| `grafana.persistence.enabled` | `true` | Dashboards + plugin state survive pod restarts. |
| `loki.persistence.enabled` | `true` | Single-binary mode at 20 GiB; switch to scalable mode above ~150 GB/day. |
| `opentelemetry-collector.mode` | `deployment` | Receives OTLP from the API pods, fan-outs to Prometheus + Loki + Tempo. |

The SI-PS API pods emit OTel telemetry on the `Cnas.Ps.Subsystems` meter (see
the §"Metrics (R0040 partial close)" table above) and on the standard
ASP.NET Core meter set. The collector scrapes `:9464/metrics` for Prometheus
ingestion and forwards logs to Loki. Grafana provisions dashboards from
ConfigMaps tagged `grafana_dashboard=1` — the dashboard library is owned by
the umbrella deployment workflow and is out of scope for this base chart.

## HA + autoscaling (R2117/R2118)

> Closes TOR ARH 008 (zero single-point-of-failure) and ARH 009 (rational
> resource use, HPA/VPA). The chart at `ops/k8s/cnas-ps/` ships every
> autoscaler off-by-default so dev / staging keep a small footprint, then
> turns them on under the `values.production.yaml` overlay.

### Postgres — Patroni HA (R2117)

The chart renders a Patroni-managed StatefulSet
(`templates/postgres-statefulset.yaml`) using the Zalando Spilo image. The
defaults boot 3 replicas with preferred host spread; the production overlay
upgrades the spread to `required` (3+ worker nodes assumed). Failover is
driven by Patroni's Kubernetes-API election: the `role: master` label moves
to the new leader and the rw service follows.

Operator commands:

- `kubectl get pod -l app.kubernetes.io/component=postgres` — observe pods.
- `kubectl get endpoints cnas-ps-postgres-rw` — should resolve to the leader.
- `patronictl -c /etc/patroni.yml list` (exec-into the leader) — full cluster.

Backups are out of scope for this section — see the dedicated backup runbook
above.

### API — HPA (existing) + VPA (R2118)

| Autoscaler | Template | Toggle | Default |
|------------|----------|--------|---------|
| HPA | `templates/api-hpa.yaml` | `hpa.enabled` | Enabled (min 3 / max 10) |
| VPA | `templates/vpa.yaml` | `autoscaling.vpa.enabled` | Disabled in base, enabled `updateMode: Off` in production overlay |

HPA targets CPU 70% / memory 80% utilisation. VPA owns the **requests**
that define the utilisation denominator, so the two compose without
competing (the upstream VPA docs codify this layout). Recommended rollout:

1. Install the VPA controller in the cluster (`vertical-pod-autoscaler`
   Helm chart or the upstream YAML bundle).
2. Render the chart with the production overlay
   (`autoscaling.vpa.enabled: true`, `updateMode: "Off"`).
3. Observe `kubectl describe vpa cnas-ps-api -n cnas-ps` for ≥ 7 days; the
   `Recommendation` block lists target requests per container.
4. Flip `updateMode: Auto` to let VPA evict pods to apply the new requests
   (HPA continues to handle horizontal scale-out unchanged).

`autoscaling.vpa.minAllowed` / `maxAllowed` bound the recommender so a
runaway memory leak cannot escalate the request to the entire node. The
production overlay caps memory at 8 GiB per pod.

### Zero-SPOF audit

| Tier | Failover unit | Replica count (prod) | Spread |
|------|---------------|----------------------|--------|
| API  | k8s Deployment + HPA | 3-10 | `requiredDuringScheduling` host spread |
| Postgres | Patroni StatefulSet | 3 | `required` |
| MinIO | StatefulSet (distributed) | 4 | preferred |
| Ingress | nginx-ingress-controller | external (cluster-managed) | — |

No tier ships with a single replica in production. The PDB enforces
`minAvailable: 2` on the API so a node drain cannot reduce the live count
below the failover floor.
