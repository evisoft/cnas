# Guaranteed performance KPIs

> Anchored to TOR PSR 001-003, PSR 008, PSR 009, PSR 010 — the values the
> supplier guarantees for SI „Protecția Socială". Implementation
> references are file paths. Companion to [`performance.md`](performance.md)
> (SLO declaration) and [`performance-ops.md`](performance-ops.md).

## 1. Scope

This document publishes the numeric performance commitments that bind the
supplier (per PSR 008). It is consumed by CNAS, by the load-test harness,
and by the alerting rules. Edits are contract amendments — see Validation.

## 2. Objectives

- One canonical KPI table per TOR clause.
- Each KPI has a target, a measurement source (metric name from
  `CnasMeter` or `SloRegistry`), and a validation method.
- Architecture tests pin numeric thresholds against accidental drift.

## 3. Implementation map

| KPI surface | Where |
|---|---|
| Latency SLO constants | [`Core/Performance/SloRegistry.cs`](../src/Cnas.Ps.Core/Performance/SloRegistry.cs) |
| Architecture pin | [`tests/Cnas.Ps.Architecture.Tests/Performance/SloRegistryTests.cs`](../tests/Cnas.Ps.Architecture.Tests/Performance/SloRegistryTests.cs) |
| Telemetry counters | [`Infrastructure/Observability/CnasMeter.cs`](../src/Cnas.Ps.Infrastructure/Observability/CnasMeter.cs) (meter `Cnas.Ps.Subsystems`) |
| Connection pool sizing | [`Infrastructure/Persistence/PostgresPoolOptions.cs`](../src/Cnas.Ps.Infrastructure/Persistence/PostgresPoolOptions.cs) |
| k6 baseline harness | [`perf/cnas-baseline.js`](../perf/cnas-baseline.js) |
| k6 CI gate | [`.github/workflows/perf-smoke.yml`](../.github/workflows/perf-smoke.yml) |

## 4. Procedure — KPI table

### 4.1 Latency (PSR 001 / PSR 010)

| TOR | Surface | Percentile | Target | Constant | Validation |
|---|---|---|---|---|---|
| PSR 001 | Ordinary requests | p90 | 1 000 ms | `SloRegistry.PSR_001_DEFAULT_P90` | k6 thresholds in `perf/cnas-baseline.js` |
| PSR 001 | Ordinary requests | p99 | 3 000 ms | `SloRegistry.PSR_001_DEFAULT_P99` | k6 thresholds |
| PSR 001 | Reports (CSV / XLSX / PDF) | p95 | 5 000 ms | `SloRegistry.PSR_001_REPORT_P95` | k6 `doc_render` scenario |
| PSR 010 | Document operations | p90 | 3 000 ms | `SloRegistry.PSR_010_DOC_P90` | k6 thresholds |
| PSR 010 | Document operations | p99 | 8 000 ms | `SloRegistry.PSR_010_DOC_P99` | k6 thresholds |

### 4.2 Concurrency (PSR 002 / PSR 003)

| TOR | Target | Constant | Validation method |
|---|---|---|---|
| PSR 002 | 1 500 authenticated users | `SloRegistry.ConcurrentAuthorizedTarget` | k6 load scenario — **PARTIAL**, see [TODO.md](../TODO.md) R2171. |
| PSR 002 | 500 anonymous users | `SloRegistry.ConcurrentAnonymousTarget` | k6 load scenario — **PARTIAL**, R2171. |
| PSR 003 | 2 000 concurrent sessions | `SloRegistry.ConcurrentSessionsTarget` | k6 soak — **PARTIAL**, R2172. Pool sized to 2 000 in `PostgresPoolOptions.MaxPoolSize`; PgBouncer `max_client_conn=2500`. |

### 4.3 Volume (PSR 009)

| TOR | Target | Constant | Validation method |
|---|---|---|---|
| PSR 009 | 300 000 transactions / day | `SloRegistry.DailyTransactionTarget` | k6 soak — **PARTIAL**, see R2177. Transactional counters in `CnasMeter` (`cnas.declaration.*`, `cnas.claim.*`, `cnas.treasury.distributed`, etc.) feed the daily total when wired to Prometheus. |

### 4.4 Peak hours (PSR 004)

| TOR | Window | Source |
|---|---|---|
| PSR 004 | 08:00-18:00 Europe/Chisinau, business days | `PeakHourGateOptions` defaults; `JobScheduleProfileRegistry.Defaults` declares `OffPeakOnly` jobs. Validated by `PeakHourGateTests`. |

## 5. Validation

- **Static pin.** Every numeric KPI is duplicated in
  `tests/Cnas.Ps.Architecture.Tests/Performance/SloRegistryTests.cs`.
  Changing a constant without updating the test fails CI. Changes require
  a contract amendment (PSR 008).
- **Latency.** The k6 harness in `perf/cnas-baseline.js` asserts
  thresholds inline; `.github/workflows/perf-smoke.yml` runs it against
  staging on every PR touching the API. The gate is skip-if-unset
  (`STAGING_BASE_URL`) so forks do not block.
- **Concurrency / volume.** A high-volume load lab (1 500 + 500 users,
  300 k tx/day) is **NOT YET COMMITTED**. The k6 scripts at
  `perf/cnas-load.js` / `perf/cnas-soak.js` are not yet in the repo. This
  is the explicit gap tracked in TODO.md R2171 / R2172 / R2177; closing
  them requires CNAS staging + a dedicated load runner. PSR 008 still
  requires the supplier to publish the targets — which this document does
  — even before the soak harness lands.
- **Telemetry.** All counters listed above are exported via the OTLP
  exporter wired by `AddCnasObservability`; Prometheus / Grafana
  manifests are tracked in TODO.md R2182.

## 6. References

- TOR PSR 001 (latency), PSR 002 (concurrency), PSR 003 (sessions),
  PSR 008 (published KPIs), PSR 009 (daily volume), PSR 010 (doc ops).
- [`performance.md`](performance.md), [`performance-ops.md`](performance-ops.md),
  [`operations.md`](operations.md).
- Iteration notes: iter 78 (`SloRegistry` + k6 baseline), iter 67
  (peak-hour gate), iter 84 (long-running report marker).
