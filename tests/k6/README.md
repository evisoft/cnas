# k6 performance test suite — CNAS „Protecția Socială"

> Anchored to TOR ID(s): R2705 (verification gate) + R2171 / R2172 / R2177
> (PSR 002, PSR 003, PSR 009). Iteration 104.

## 1. Purpose

This directory hosts the **capacity-tier** k6 scenarios that prove the
platform meets the TOR-mandated load envelope:

| Script | TOR | Target |
|---|---|---|
| `capacity-1500-auth-500-anon.js` | R2171 / PSR 002 | 1500 authorised + 500 anonymous users, steady state |
| `concurrent-sessions-2000.js`    | R2172 / PSR 003 | 2000 concurrent VUs, spike + hold |
| `daily-throughput-300k.js`       | R2177 / PSR 009 | 300 000 transactions / day sustained |

The lightweight smoke harness lives at `perf/cnas-baseline.js` (PSR 001 /
PSR 010, p90 ≤ 1 s) and runs in CI via
`.github/workflows/perf-smoke.yml`. These capacity scripts are heavier
and are **operator-triggered only** — see §5.

## 2. Audience

- Supplier perf engineering team.
- CNAS DevOps / SRE shift leads.
- Acceptance committee for the R2705 verification gate.

## 3. Prerequisites

- `k6` binary ≥ 0.50 installed locally
  (`brew install k6` / `choco install k6` / download from k6.io).
- Network reachability to the staging deployment (the production VPN is
  not used — see `docs/operations/mcloud-environments.md`).
- `BASE_URL` env var set to the staging base URL
  (e.g. `https://staging.cnas.gov.md`).
- Optional `AUTH_TOKEN` env var for the authorised lanes. When unset,
  the scripts tolerate `401` responses so they remain usable as
  unauthenticated harnesses (latency is still measured).

## 4. Running locally

```bash
# Steady-state capacity (R2171 / PSR 002)
BASE_URL=https://staging.cnas.gov.md \
  k6 run tests/k6/capacity-1500-auth-500-anon.js

# Concurrent-session spike (R2172 / PSR 003)
BASE_URL=https://staging.cnas.gov.md \
  k6 run tests/k6/concurrent-sessions-2000.js

# Daily-throughput sample (R2177 / PSR 009)
BASE_URL=https://staging.cnas.gov.md \
  k6 run tests/k6/daily-throughput-300k.js
```

Add `-e AUTH_TOKEN=eyJhbGc...` for the authorised lanes. Use
`--summary-export=summary.json` to capture the result for the
Acceptance Protocol.

## 5. CI policy

These scripts are **committed but NOT auto-executed in CI**. Reasons:

- They produce real load (≥ 2000 VUs at peak) that must hit only an
  agreed environment with capacity reservation.
- The default GitHub-hosted runners do not ship the `k6` binary and
  cannot saturate the link budget anyway.
- Production runs need a paired observability window and on-call cover
  (see `docs/operations/mcloud-environments.md`).

The k6 cloud / Grafana k6 integration is a separate iteration. Until
then, runs are operator-triggered from a dedicated load-lab worker.

## 6. Endpoint mix

All three scripts share the same realistic mix derived from current
public surfaces in `src/Cnas.Ps.Api/Controllers/`:

| Endpoint | Surface |
|---|---|
| `GET /api/public/content` | Public CMS content (anonymous) |
| `GET /api/public-catalog` | Public service catalogue (anonymous) |
| `POST /api/applications` | Submit a Cerere (authorised) |
| `GET  /api/applications/mine` | List own Cereri (authorised) |

The POST payload is intentionally minimal — the scenarios measure
**transport + dispatch** latency, not full workflow throughput. End-to-end
journey perf is covered by the smoke E2E (R2707) and the dedicated
`SloRegistry` budgets.

## 7. Thresholds rationale

Each script declares thresholds aligned with the corresponding TOR
clause (PSR 002 / 003 / 009). The detailed rationale is inline in each
file. The global ceiling stays at `http_req_failed < 1 %` for the
steady-state and concurrent suites, and `< 0.1 %` for the sustained
daily-throughput suite (errors compound at 300k/day scale).

## 8. References

- TOR §PSR 001-015 (performance + scalability).
- `src/Cnas.Ps.Core/Performance/SloRegistry.cs` — single source of
  truth for latency budgets.
- `docs/performance-kpis.md` — KPI table.
- `docs/performance-ops.md` — operations playbook.
- `perf/cnas-baseline.js` — companion smoke harness (PSR 001).
