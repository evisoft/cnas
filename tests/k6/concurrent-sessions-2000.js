// SPDX-License-Identifier: MIT
// CNAS „Protecția Socială" — concurrent-sessions spike test.
// Anchored to TOR R2172 / PSR 003.
//
// Usage:
//   BASE_URL=https://staging.cnas.gov.md \
//     k6 run tests/k6/concurrent-sessions-2000.js
//
//   Optional: -e AUTH_TOKEN=eyJhbGc... for the authorised half.
//
// Scenario shape
// --------------
//   Ramp from 0 to 2000 concurrent VUs in 3 min, hold for 5 min, then drain.
//   This proves the platform sustains the PSR 003 concurrent-session ceiling
//   without the connection pool, thread-pool, or session limiter
//   (SEC 017 / R2264) starving the request handlers.
//
// Thresholds (PSR 003 + PSR 001)
// ------------------------------
//   * p99 < 1000 ms              — tighter than the PSR 001 p99 < 3000 ms
//                                   ordinary ceiling so the tail latency
//                                   regression is visible *before* the SLO
//                                   trips.
//   * error rate < 1 %           — global safety net.
//   * Hold duration 5 min        — the SessionAutoLockJob runs every 5 min
//                                   (SessionLimitOptions.IdleLockMinutes), so
//                                   a full hold cycle proves the lock sweep
//                                   does not destabilise the system under
//                                   load.

import http from 'k6/http';
import { check, group, sleep } from 'k6';

// ---- Configuration --------------------------------------------------------

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const AUTH_TOKEN = __ENV.AUTH_TOKEN || '';

export const options = {
    scenarios: {
        spike_2000: {
            executor: 'ramping-vus',
            startVUs: 0,
            // 2000 concurrent VUs = the PSR 003 ceiling. Ramp deliberately
            // steep so the connection pool and thread pool both feel the
            // pressure inside the hold window.
            stages: [
                { duration: '3m', target: 2000 },
                { duration: '5m', target: 2000 },
                { duration: '2m', target: 0 },
            ],
            exec: 'mixedFlow',
            gracefulStop: '30s',
        },
    },
    thresholds: {
        // PSR 003 tail-latency budget. Tighter than the PSR 001 p99 ceiling
        // so we catch the regression before the SLO breach.
        http_req_duration: ['p(99)<1000'],
        // Global safety net.
        http_req_failed: ['rate<0.01'],
    },
};

// ---- Helpers --------------------------------------------------------------

/**
 * Build a request-headers map. Half the VUs send Authorization (when the
 * token is provided), the other half stay anonymous, which approximates
 * the PSR 002 mix at PSR 003 scale.
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
 * Each VU rotates through the same 4-step mix. VU index parity drives the
 * auth/anonymous split so the load splits ~50/50 deterministically.
 */
export function mixedFlow() {
    const authorisedVu = (__VU % 2) === 0;

    group('public_content', function () {
        const res = http.get(
            `${BASE_URL}/api/public/content`,
            buildHeaders(false),
        );
        check(res, {
            'content status is 200': (r) => r.status === 200,
        });
    });

    group('public_catalog', function () {
        const res = http.get(
            `${BASE_URL}/api/public-catalog`,
            buildHeaders(false),
        );
        check(res, {
            'catalog status is 200': (r) => r.status === 200,
        });
    });

    if (authorisedVu) {
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

        group('submit_application', function () {
            const payload = JSON.stringify({
                serviceCode: 'PUB-CAT-SPIKE',
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

    // Short think-time — the spike intentionally produces high RPS.
    sleep(0.5);
}
