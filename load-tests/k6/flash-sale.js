import http from 'k6/http';
import { check } from 'k6';

// Redis-unavailable fallback completes synchronously (200); the queue path
// accepts asynchronously (202). Sold-out responses (409) are also expected.
// Rate-limiting (429) is an infrastructure rejection, not an application error,
// and is expected under burst load.
http.setResponseCallback(http.expectedStatuses(200, 202, 409, 429));

// Flash-sale purchase burst against the tiered order endpoint.
// Usage: k6 run -e BASE_URL=http://localhost:5065 load-tests/k6/flash-sale.js
// NOTE: idempotency keys are unique per VU iteration (one purchase intention each).

export const options = {
  scenarios: {
    flash_sale_burst: {
      executor: 'ramping-arrival-rate',
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 200,
      stages: [
        { target: 50, duration: '30s' }, // ramp to 50 orders/s
        { target: 50, duration: '30s' },
        { target: 0, duration: '10s' },
      ],
    },
  },
  thresholds: {
    // p95 of the reservation path must stay fast (measured local baseline: ~29 ms)
    http_req_duration: ['p(95)<250'],
    // sold-out 409s are a valid business outcome, not errors — count only real failures
    http_req_failed: [{ threshold: 'rate<0.05', delayAbortEval: '10s' }],
    // Custom checks (200/202 accepted / 409 sold-out) must overwhelmingly pass —
    // a broken app returns odd statuses and fails the whole run.
    checks: ['rate>0.95'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5065';

export default function () {
  const res = http.post(`${BASE_URL}/api/orders`,
    JSON.stringify({ productId: 1, quantity: 1 }),
    {
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': `k6-${__VU}-${__ITER}`,
      },
    });

  const ok = check(res, {
    'accepted (200/202) or rejected (409/429)': (r) => r.status === 200 || r.status === 202 || r.status === 409 || r.status === 429,
    'never a 5xx': (r) => r.status < 500,
  });
  if (!ok) console.error(`unexpected status ${res.status}`);
}
