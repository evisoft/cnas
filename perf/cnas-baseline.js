// =============================================================================
// CNAS „Protecția Socială" — k6 baseline performance harness
//
// Purpose
// -------
// Smoke-level performance script that exercises the most important request
// shapes against a *staging* deployment of SI „Protecția Socială" and asserts
// the latency SLOs declared in TOR PSR 001 / PSR 010.
//
// The thresholds below must stay in lockstep with
// `src/Cnas.Ps.Core/Performance/SloRegistry.cs`. The architecture test
// `SloRegistryTests.PerfHarness_Declares_Default_P90_Threshold` locks the
// `p(90)<1000` string in this file so accidental drift is caught at PR time.
//
// Scenarios
// ---------
//   1. anonymous_public      — GET  /api/public-catalog
//   2. authenticated_search  — POST /api/solicitants/search
//   3. admin_search          — POST /api/admin/audit/search
//   4. doc_render            — GET  /api/public-catalog/export.csv
//                              (CSV export — placeholder for document rendering)
//
// Each scenario maps to a k6 group; thresholds are attached per-group so a
// regression in one surface fails the run cleanly without masking by averaging
// over the others.
//
// How to run locally
// ------------------
//   # Smoke run against a deployed staging URL:
//   k6 run -e BASE_URL=https://staging.cnas.gov.md perf/cnas-baseline.js
//
//   # Smoke run with auth token (skip the auth scenario if not provided):
//   k6 run -e BASE_URL=https://staging.cnas.gov.md \
//          -e AUTH_TOKEN=eyJhbGc... \
//          perf/cnas-baseline.js
//
// CI execution
// ------------
// `.github/workflows/perf-smoke.yml` runs this script on PRs that touch
// `src/Cnas.Ps.Api/**`, BUT only when the repository variable
// `STAGING_BASE_URL` is configured. PRs from forks (which do not have access
// to repository variables) therefore skip the gate cleanly rather than fail.
//
// Bounded duration
// ----------------
// The `stages` block lasts ~3 minutes — this script is *not* a 24-hour soak.
// PSR 008 / PSR 014 long-duration testing is tracked separately (R2174 docs +
// future load lab batches).
// =============================================================================

import http from 'k6/http';
import { check, group, sleep } from 'k6';

// ---- Configuration ---------------------------------------------------------

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const AUTH_TOKEN = __ENV.AUTH_TOKEN || '';

// Per-scenario VU counts kept low: this is a smoke harness, not a load test.
// A future batch will raise these to the PSR 002 / PSR 003 targets
// (1500 authorised + 500 anonymous + 2000 concurrent sessions).
export const options = {
    scenarios: {
        anonymous_public: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '30s', target: 5 },
                { duration: '60s', target: 10 },
                { duration: '30s', target: 0 },
            ],
            exec: 'anonymousPublic',
            gracefulStop: '10s',
        },
        authenticated_search: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '30s', target: 3 },
                { duration: '60s', target: 5 },
                { duration: '30s', target: 0 },
            ],
            exec: 'authenticatedSearch',
            gracefulStop: '10s',
        },
        admin_search: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '30s', target: 2 },
                { duration: '60s', target: 3 },
                { duration: '30s', target: 0 },
            ],
            exec: 'adminSearch',
            gracefulStop: '10s',
        },
        doc_render: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '30s', target: 2 },
                { duration: '60s', target: 3 },
                { duration: '30s', target: 0 },
            ],
            exec: 'docRender',
            gracefulStop: '10s',
        },
    },
    // Thresholds mirror SloRegistry.cs:
    //   * Ordinary requests: p(90)<1000, p(99)<3000  (PSR 001)
    //   * Report endpoints:  p(95)<5000              (PSR 001)
    //   * Document ops:      p(90)<3000, p(99)<8000  (PSR 010)
    thresholds: {
        'http_req_duration{group:anonymous_public}': ['p(90)<1000', 'p(99)<3000'],
        'http_req_duration{group:authenticated_search}': ['p(90)<1000', 'p(99)<3000'],
        'http_req_duration{group:admin_search}': ['p(90)<1000', 'p(99)<3000'],
        'http_req_duration{group:doc_render}': ['p(90)<3000', 'p(99)<8000'],
        // Global safety net — error rate must stay below 1% across the run.
        http_req_failed: ['rate<0.01'],
    },
};

// ---- Helpers --------------------------------------------------------------

function authHeaders() {
    const headers = { 'Content-Type': 'application/json' };
    if (AUTH_TOKEN) {
        headers['Authorization'] = `Bearer ${AUTH_TOKEN}`;
    }
    return headers;
}

// ---- Scenario: anonymous public catalogue ---------------------------------

/**
 * Anonymous browse of the public service catalogue.
 * Maps to PSR 001 anonymous lane (PSR 002: 500 concurrent anonymous).
 */
export function anonymousPublic() {
    group('anonymous_public', function () {
        const res = http.get(`${BASE_URL}/api/public-catalog`);
        check(res, {
            'status is 200': (r) => r.status === 200,
        });
    });
    sleep(1);
}

// ---- Scenario: authenticated solicitant search ----------------------------

/**
 * Logged-in caseworker performs a QBE search over solicitants.
 * Maps to PSR 001 authorised lane (PSR 002: 1500 concurrent authorised).
 */
export function authenticatedSearch() {
    group('authenticated_search', function () {
        const payload = JSON.stringify({
            page: 1,
            pageSize: 25,
            filters: {},
        });
        const res = http.post(
            `${BASE_URL}/api/solicitants/search`,
            payload,
            { headers: authHeaders() },
        );
        // 401 is acceptable when AUTH_TOKEN is not supplied — we still measure
        // latency, which is the SLO we are guarding. The status check below
        // tolerates 200 + 401 to keep the harness usable without secrets.
        check(res, {
            'status is 200 or 401': (r) => r.status === 200 || r.status === 401,
        });
    });
    sleep(1);
}

// ---- Scenario: admin audit search -----------------------------------------

/**
 * Administrator searches the audit log. The audit explorer is one of the
 * heaviest authorised surfaces, so it carries its own latency budget here.
 */
export function adminSearch() {
    group('admin_search', function () {
        const payload = JSON.stringify({
            page: 1,
            pageSize: 25,
        });
        const res = http.post(
            `${BASE_URL}/api/admin/audit/search`,
            payload,
            { headers: authHeaders() },
        );
        check(res, {
            'status is 200, 401, or 403': (r) =>
                r.status === 200 || r.status === 401 || r.status === 403,
        });
    });
    sleep(1);
}

// ---- Scenario: document rendering placeholder -----------------------------

/**
 * CSV export of the public catalogue — the cheapest available "document
 * rendering" surface. Real PDF / XLSX renders will be added in a future
 * batch (tracked under PSR 010 / document subsystem work).
 */
export function docRender() {
    group('doc_render', function () {
        const res = http.get(`${BASE_URL}/api/public-catalog/export.csv`);
        check(res, {
            'status is 200': (r) => r.status === 200,
        });
    });
    sleep(2);
}
