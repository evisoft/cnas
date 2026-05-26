// SPDX-License-Identifier: MIT
// CNAS „Protecția Socială" — capacity test (1500 authorised + 500 anonymous).
// Anchored to TOR R2171 / PSR 002.
//
// Usage:
//   BASE_URL=https://staging.cnas.gov.md \
//     k6 run tests/k6/capacity-1500-auth-500-anon.js
//
//   Optional: -e AUTH_TOKEN=eyJhbGc... for the authorised lane.
//
// Scenarios
// ---------
//   1. authorised  — 1500 VUs, mixed POST /api/applications + GET /api/applications/mine
//   2. anonymous   — 500 VUs, mixed GET /api/public/content + GET /api/public-catalog
//
// Thresholds (PSR 001 + PSR 002)
// ------------------------------
//   * p95 < 500 ms                — well inside the PSR 001 p90 < 1000 ms ceiling
//                                    so a regression here surfaces before the SLO
//                                    breach in `SloRegistry`.
//   * error rate < 1 %            — matches the existing `perf/cnas-baseline.js`
//                                    global safety net.
//   * Steady-state for 10 min     — long enough to exhaust the EF Core connection
//                                    pool warm-up window and surface GC pauses.

import http from 'k6/http';
import { check, group, sleep } from 'k6';

// ---- Configuration --------------------------------------------------------

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const AUTH_TOKEN = __ENV.AUTH_TOKEN || '';

// PSR 002 capacity envelope. Held flat for 10 min after a 2-min ramp so the
// p95 measurement is dominated by steady-state and not by warm-up cold paths.
export const options = {
    scenarios: {
        authorised: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '2m', target: 1500 },
                { duration: '10m', target: 1500 },
                { duration: '1m', target: 0 },
            ],
            exec: 'authorisedFlow',
            gracefulStop: '30s',
        },
        anonymous: {
            executor: 'ramping-vus',
            startVUs: 0,
            stages: [
                { duration: '2m', target: 500 },
                { duration: '10m', target: 500 },
                { duration: '1m', target: 0 },
            ],
            exec: 'anonymousFlow',
            gracefulStop: '30s',
        },
    },
    thresholds: {
        // PSR 002 — capacity. p95 well below the PSR 001 p90 ceiling so we
        // catch regressions before the SLO breach.
        'http_req_duration{lane:authorised}': ['p(95)<500'],
        'http_req_duration{lane:anonymous}': ['p(95)<500'],
        // Global safety net — the suite fails if more than 1 % of requests error.
        http_req_failed: ['rate<0.01'],
    },
};

// ---- Helpers --------------------------------------------------------------

/**
 * Build a request-headers map with optional bearer token. Tags every request
 * with the scenario lane so per-lane thresholds resolve correctly.
 */
function buildHeaders(lane, includeAuth) {
    const headers = { 'Content-Type': 'application/json' };
    if (includeAuth && AUTH_TOKEN) {
        headers['Authorization'] = `Bearer ${AUTH_TOKEN}`;
    }
    return { headers, tags: { lane: lane } };
}

// ---- Authorised flow (1500 VUs) ------------------------------------------

/**
 * Mixed authorised flow: list own applications, then submit a minimal
 * Cerere payload. Tolerates 401 when AUTH_TOKEN is unset so the harness
 * stays runnable without secrets — latency is still measured.
 */
export function authorisedFlow() {
    group('list_own_applications', function () {
        const res = http.get(
            `${BASE_URL}/api/applications/mine`,
            buildHeaders('authorised', true),
        );
        check(res, {
            'list status is 200 or 401': (r) => r.status === 200 || r.status === 401,
        });
    });

    group('submit_application', function () {
        const payload = JSON.stringify({
            serviceCode: 'PUB-CAT-SMOKE',
            formData: {},
            attachments: [],
        });
        const res = http.post(
            `${BASE_URL}/api/applications`,
            payload,
            buildHeaders('authorised', true),
        );
        // 400 acceptable too — the request body is a placeholder by design.
        check(res, {
            'submit status is 200, 201, 400, or 401': (r) =>
                r.status === 200 || r.status === 201 || r.status === 400 || r.status === 401,
        });
    });

    // Realistic think-time so 1500 VUs do not become 1500 req/s.
    sleep(1);
}

// ---- Anonymous flow (500 VUs) --------------------------------------------

/**
 * Public surface mix: CMS content + service catalogue. Anonymous lane has
 * no auth header by definition.
 */
export function anonymousFlow() {
    group('public_content', function () {
        const res = http.get(
            `${BASE_URL}/api/public/content`,
            buildHeaders('anonymous', false),
        );
        check(res, {
            'content status is 200': (r) => r.status === 200,
        });
    });

    group('public_catalog', function () {
        const res = http.get(
            `${BASE_URL}/api/public-catalog`,
            buildHeaders('anonymous', false),
        );
        check(res, {
            'catalog status is 200': (r) => r.status === 200,
        });
    });

    sleep(2);
}
