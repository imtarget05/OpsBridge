import { expect, test } from '@playwright/test';
import { Client } from 'pg';
import { apiLogin, signInAs, UI_TIMEOUT } from './support';

/**
 * THE DEPLOYED-ARTEFACT SPEC.
 *
 * The other four specs prove the PRODUCT works. They prove it against
 * whatever answers on the configured base URL - and until now that was always
 * the Vite dev server, which proxies /api to the API from
 * `vite.config.ts`. A dev server is not the artefact that gets deployed: it
 * has no nginx, it has no security headers, and it has its own /api proxy.
 * A green dev-server run therefore says nothing about the deployed bundle.
 *
 * This spec closes that gap. It only runs when PLAYWRIGHT_BASE_URL points at
 * an EXTERNAL deployment (the containerised stack of Dockerfile.web: nginx in
 * front of the API), and it then asserts:
 *
 *   1. the response really came from nginx (the `Server` header nginx sets on
 *      every response it produces) and not from a dev server,
 *   2. the security headers the image declares are actually on the wire,
 *   3. the SPA fallback works (a deep link returns the app, not a 404),
 *   4. the API is reachable THROUGH the proxy, same-origin, and the proxy
 *      does not weaken the API's own authentication boundary,
 *   5. the write path - a click in the browser - travels through nginx into
 *      PostgreSQL, and the row is read back FROM POSTGRESQL (not through the
 *      API, which would only prove that the API agrees with itself).
 *
 * The database is read with a direct PostgreSQL connection
 * (STACK_SMOKE_DATABASE_URL) so the write is verified at the level it
 * actually happened: in the database.
 */

/** The base URL of the deployment under test (empty = the dev server). */
const EXTERNAL_BASE_URL = process.env.PLAYWRIGHT_BASE_URL?.trim() ?? '';

/** The PostgreSQL connection string of the database behind the API. */
const DATABASE_URL = process.env.STACK_SMOKE_DATABASE_URL?.trim() ?? '';

test.describe('the containerised stack: nginx in front of the API', () => {
  // The dev-server mode (the default) has no nginx, so this spec would fail
  // there for the wrong reason. It is skipped instead - and the
  // `opsbridge-stack-smoke` CI job runs it for real.
  test.skip(
    !EXTERNAL_BASE_URL,
    'Only meaningful against a deployment (set PLAYWRIGHT_BASE_URL) - the dev server has no nginx in front of it.',
  );

  test('the app is served by nginx, with the security headers of the image', async ({ page }) => {
    // ---- /healthz: the liveness endpoint the image declares --------------
    const health = await page.request.get('/healthz');
    expect(health.status()).toBe(200);
    // nginx sets this header on every response it produces itself. A Vite
    // dev server does not.
    expect(health.headers()['server']).toMatch(/nginx/i);

    // ---- the document itself ----------------------------------------------
    const document = await page.goto('/');
    expect(document?.status()).toBe(200);
    const headers = document!.headers();

    // ---- the security headers of the image --------------------------------
    expect(headers['server']).toMatch(/nginx/i);

    const csp = headers['content-security-policy'];
    expect(csp, 'the image must set a Content-Security-Policy').toBeTruthy();
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).not.toContain('unsafe-inline');
    expect(csp).not.toContain('unsafe-eval');

    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['referrer-policy']).toBeTruthy();
    expect(headers['x-frame-options']).toBeTruthy();
    expect(headers['permissions-policy']).toBeTruthy();
  });

  test('the SPA fallback serves the app on a deep link and the API is proxied', async ({
    page,
  }) => {
    // A client-side route: nginx must fall back to index.html instead of
    // answering 404 (the browser router owns the routes).
    const deepLink = await page.request.get('/purchase-orders');
    expect(deepLink.status()).toBe(200);
    expect(deepLink.headers()['content-type']).toContain('text/html');

    // The API is reached through the proxy, same-origin: the login response
    // carries nginx's Server header, which the API itself never sets.
    const login = await page.request.post('/api/v1/auth/login', {
      headers: { 'Content-Type': 'application/json' },
      data: { username: 'e2e_stack', role: 'Operator' },
    });
    expect(login.status()).toBe(200);
    expect(login.headers()['server']).toMatch(/nginx/i);
    expect(login.headers()['content-type']).toContain('application/json');
    const token = ((await login.json()) as { accessToken: string }).accessToken;
    expect(token).toBeTruthy();

    // An authenticated API call through the proxy answers with the API's own
    // payload - not with the SPA's index.html. If the proxy swallowed /api,
    // this would be a 200 with text/html.
    const metrics = await page.request.get('/api/v1/dashboard/metrics', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(metrics.status()).toBe(200);
    expect(metrics.headers()['content-type']).toContain('application/json');
    expect(metrics.headers()['server']).toMatch(/nginx/i);
    const body = (await metrics.json()) as { totalInvoices: number };
    expect(typeof body.totalInvoices).toBe('number');

    // The proxy does not weaken the API's authentication boundary: the same
    // route without the bearer token is refused by the API, through nginx.
    const anonymous = await page.request.get('/api/v1/dashboard/metrics');
    expect(anonymous.status()).toBe(401);
  });

  test('a click in the browser travels through nginx into PostgreSQL', async ({ page }) => {
    test.setTimeout(180_000);

    if (!DATABASE_URL) {
      throw new Error(
        'STACK_SMOKE_DATABASE_URL is required: the write path has to be verified in PostgreSQL, ' +
          'not through the API that just wrote it.',
      );
    }

    const orderNumber = `PO-STACK-${Date.now()}-${Math.floor(Math.random() * 1000)}`;
    const quantity = 9;
    const unitPrice = 4321;

    // The login goes through the browser, so it goes through nginx.
    await signInAs(page, 'Operator');
    await page.goto('/purchase-orders');

    await page.getByRole('button', { name: 'New Purchase Order' }).click();
    const modalTitle = page.getByRole('heading', { name: 'Create New Purchase Order' });
    await expect(modalTitle).toBeVisible({ timeout: UI_TIMEOUT });

    await page.getByLabel('PO Number', { exact: true }).fill(orderNumber);
    await page.getByLabel('Supplier Code', { exact: true }).fill('SUP-STACK-01');
    await page.getByLabel('Supplier Name', { exact: true }).fill('Stack Smoke Supplier');
    await page.getByLabel('SKU', { exact: true }).fill('SKU-STACK');
    await page.getByLabel('Description', { exact: true }).fill('Stack smoke line item');
    await page.getByLabel('Quantity (PCS)', { exact: true }).fill(String(quantity));
    await page.getByLabel('Unit Price (VND)', { exact: true }).fill(String(unitPrice));

    await page.getByRole('button', { name: 'Create PO' }).click();
    await expect(modalTitle).toBeHidden({ timeout: UI_TIMEOUT });

    // ---- the row is in PostgreSQL, read from the database itself ----------
    const client = new Client({ connectionString: DATABASE_URL });
    await client.connect();
    try {
      const result = await client.query(
        'SELECT "OrderNumber", "SupplierCode", "SupplierName", "Currency" ' +
          'FROM "PurchaseOrders" WHERE "OrderNumber" = $1',
        [orderNumber],
      );
      expect(result.rows).toHaveLength(1);
      expect(result.rows[0].OrderNumber).toBe(orderNumber);
      expect(result.rows[0].SupplierCode).toBe('SUP-STACK-01');
      expect(result.rows[0].SupplierName).toBe('Stack Smoke Supplier');
      expect(result.rows[0].Currency).toBe('VND');
    } finally {
      await client.end();
    }

    // ---- and the API serves it back through the proxy ---------------------
    const token = await apiLogin(page, 'Operator');
    const persisted = await page.request.get(`/api/v1/purchase-orders/${orderNumber}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(persisted.status()).toBe(200);
    expect(persisted.headers()['server']).toMatch(/nginx/i);
    const body = (await persisted.json()) as { orderNumber: string; totalAmount: number };
    expect(body.orderNumber).toBe(orderNumber);
    expect(body.totalAmount).toBe(quantity * unitPrice);
  });
});
