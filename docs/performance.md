# Performance — SLOs and baseline harness

> Companion to [`../README.md`](../README.md), [`ARCHITECTURE.md`](ARCHITECTURE.md),
> and [`operations.md`](operations.md). Covers the TOR PSR 001 / PSR 010
> performance contract and how it is encoded, verified, and gated in CI.

## Canonical SLO declaration

The single source of truth for performance targets is
[`src/Cnas.Ps.Core/Performance/SloRegistry.cs`](../src/Cnas.Ps.Core/Performance/SloRegistry.cs).
It exposes the contractual values as `const` fields so any downstream
system (alerting rules, dashboards, the k6 harness, future load-shedding
logic) references the same numbers.

The latency SLOs and their TOR provenance:

| Code | Surface | Percentile | Threshold | TOR clause |
|---|---|---|---|---|
| `PSR_001_DEFAULT_P90` | Ordinary requests | p90 | 1 000 ms | PSR 001 |
| `PSR_001_DEFAULT_P99` | Ordinary requests | p99 | 3 000 ms | PSR 001 |
| `PSR_001_REPORT_P95` | Report endpoints (CSV / XLSX / PDF) | p95 | 5 000 ms | PSR 001 |
| `PSR_010_DOC_P90` | Document operations | p90 | 3 000 ms | PSR 010 |
| `PSR_010_DOC_P99` | Document operations | p99 | 8 000 ms | PSR 010 |

Concurrency and volumetric targets (exposed as additional constants in the
same file, not yet enforced by the harness):

| Constant | Value | TOR clause |
|---|---|---|
| `ConcurrentAuthorizedTarget` | 1 500 | PSR 002 |
| `ConcurrentAnonymousTarget` | 500 | PSR 002 |
| `ConcurrentSessionsTarget` | 2 000 | PSR 003 |
| `DailyTransactionTarget` | 300 000 | PSR 005 |

Changing any of these values is a contract amendment. The architecture
tests in
[`tests/Cnas.Ps.Architecture.Tests/Performance/SloRegistryTests.cs`](../tests/Cnas.Ps.Architecture.Tests/Performance/SloRegistryTests.cs)
lock the thresholds so accidental edits surface in code review.

## k6 baseline harness

The smoke-level perf script lives at
[`perf/cnas-baseline.js`](../perf/cnas-baseline.js). It exercises four
scenario groups and asserts the latency thresholds inline:

1. `anonymous_public` — `GET /api/public-catalog` (public lane, PSR 002 anonymous).
2. `authenticated_search` — `POST /api/solicitants/search` (authorised lane, PSR 002).
3. `admin_search` — `POST /api/admin/audit/search` (heaviest authorised surface).
4. `doc_render` — `GET /api/public-catalog/export.csv` (placeholder for PSR 010).

Run it locally against a deployed staging URL:

```bash
k6 run -e BASE_URL=https://staging.cnas.gov.md perf/cnas-baseline.js
```

A bounded run takes roughly three minutes per scenario. The script is **not
a soak test** — long-duration testing (PSR 008 / PSR 014) will land in a
separate batch.

## CI gate

[`.github/workflows/perf-smoke.yml`](../.github/workflows/perf-smoke.yml)
runs the k6 harness on every PR that touches `src/Cnas.Ps.Api/**`,
`perf/**`, or the workflow itself. The gate is skip-if-unset: if the
repository variable `STAGING_BASE_URL` is not configured (e.g. PRs from
forks), the job posts a `::notice::` and exits successfully so it never
blocks community contributions. Once `STAGING_BASE_URL` is set under
*Settings → Variables → Actions*, the gate becomes mandatory.

## Deferred work

- Live latency telemetry via Prometheus + Grafana dashboards
  (`grafana_dashboard.json` artefact in `ops/grafana/`).
- Alerting rules wired off `SloRegistry.All()` (PrometheusRule manifests).
- Adaptive load-shedding at the API gateway when the rolling p99 breaches
  `DefaultP99LatencyMs`.
- High-volume load lab — the 1 500 + 500 concurrency and 300 000
  transactions-per-day targets are documented here but not yet validated
  by an automated harness.

Each of the above is tracked as a follow-up `Rxxxx` entry in
[`TODO.md`](../TODO.md) under §15.5.
