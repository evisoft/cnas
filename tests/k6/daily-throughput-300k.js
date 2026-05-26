// SPDX-License-Identifier: MIT
// CNAS „Protecția Socială" — sustained-throughput test (300 000 tx/day).
// Anchored to TOR R2177 / PSR 009.
//
// Usage:
//   BASE_URL=https://staging.cnas.gov.md \
//     k6 run tests/k6/daily-throughput-300k.js
//
//   Optional: -e AUTH_TOKEN=eyJhbGc... for the authorised lane.
//
// Arithmetic
// ----------
//   PSR 009 target = 300 000 tx/day
//                  = 300 000 / 86 400 s
//                  ≈ 3.4722 req/s
//   We round to 4 req/s for headroom (covers PSR 009 + reporting noise).
//
// Scenario shape
// --------------
//   Constant-arrival-rate, 4 req/s for a sample 30-min window. The
//   30-min sample is the smallest run that surfaces the steady-state
//   GC / connection-recycle behaviour while staying inside the
//   operator-triggered load-lab budget; extrapolate to 24 h via the
//   `iterations` and `http_reqs` metrics in the summary export.
//
// Thresholds (PSR 009 + PSR 001)
// ------------------------------
//   * error rate < 0.1 %         — at 300 k/day, 1 % errors = 3 000 failed
//                                   transactions/day, which is unacceptable
//                                   for the steady-state surface; tighten
//                                   the safety net by 10× versus the
//                                   capacity/spike suites.
//   * p95 < 1000 ms               — matches the PSR 001 p90 ceiling at one
//                                   tier higher (p95). Sustained traffic
//                                   should not push the tail past the
//                                   ordinary p90 budget.

import http from 'k6/http';
import { check, group, sleep } from 'k6';

// ---- Configuration --------------------------------------------------------

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const AUTH_TOKEN = __ENV.AUTH_TOKEN || '';

// 300 000 tx/day ≈ 3.47 req/s. We push 4 req/s for ~15 % headroom.
const TARGET_RPS = 4;
const SAMPLE_DURATION = '30m';

export const options = {
    scenarios: {
        sustained_throughput: {
            executor: 'constant-arrival-rate',
            rate: TARGET_RPS,
            timeUnit: '1s',
            duration: SAMPLE_DURATION,
            // preAllocatedVUs is the executor's worker-pool seed. Set
            // comfortably above TARGET_RPS so a brief tail latency spike
            // does not starve the arrival schedule.
            preAllocatedVUs: 20,
            maxVUs: 50,
            exec: 'mixedFlow',
            gracefulStop: '30s',
        },
    },
    thresholds: {
        // PSR 009 — error rate at sustained scale must be tight.
        http_req_failed: ['rate<0.001'],
        // PSR 001 — tail should remain inside the ordinary p90 ceiling.
        http_req_duration: ['p(95)<1000'],
    },
};

// ---- Helpers --------------------------------------------------------------

/**
 * Build a request-headers map. Authorised lane carries the bearer token
 * when supplied; anonymous lane has no Authorization header.
 */
function buildHeaders(includeAuth) {
    const headers = { 'Content-Type': 'application/json' };
    if (includeAuth && AUTH_TOKEN) {
        headers['Authorization'] = `Bearer ${AUTH_TOKEN}`;
    }
    return { headers };
}

// ---- Mixed flow -----------------------------------------------------------

/**
 * Rotates through GET /api/public/content, GET /api/applications/mine,
 * and POST /api/applications based on iteration index. This produces
 * a 2 : 1 : 1 read/list/write mix, which matches the production
 * shape declared in `docs/performance-kpis.md`.
 */
export function mixedFlow() {
    const slot = __ITER % 4;

    if (slot === 0 || slot === 1) {
        group('public_content', function () {
            const res = http.get(
                `${BASE_URL}/api/public/content`,
                buildHeaders(false),
            );
            check(res, {
                'content status is 200': (r) => r.status === 200,
            });
        });
    } else if (slot === 2) {
        group('list_own_applications', function () {
            const res = http.get(
                `${BASE_URL}/api/applications/mine`,
                buildHeaders(true),
            );
            check(res, {
                'list status is 200 or 401': (r) =>
                    r.status === 200 || r.status === 401,
            });
        });
    } else {
        group('submit_application', function () {
            const payload = JSON.stringify({
                serviceCode: 'PUB-CAT-SUSTAINED',
                formData: {},
                attachments: [],
            });
            const res = http.post(
                `${BASE_URL}/api/applications`,
                payload,
                buildHeaders(true),
            );
            check(res, {
                'submit status acceptable': (r) =>
                    r.status === 200 || r.status === 201 ||
                    r.status === 400 || r.status === 401,
            });
        });
    }

    // No explicit sleep — the constant-arrival-rate executor controls
    // pacing globally.
}
