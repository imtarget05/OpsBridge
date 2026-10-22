import { expect, test } from '@playwright/test';
import { apiGet, apiLogin, seedProcurementDocuments, signInAs, UI_TIMEOUT } from './support';

/**
 * REQ-17 smoke journey: the Operator persona must be able to reach EVERY
 * authenticated screen of the application, and every screen must render data
 * that came from the real API (and therefore from the real PostgreSQL).
 *
 * The eight routes are the ones registered in `src/App.tsx`; the sidebar link
 * labels come from `src/components/layout/Sidebar.tsx` and the page headings
 * from the `h1` of each feature page.
 *
 * WHY THIS SPEC SEEDS ITS OWN DATA
 * --------------------------------
 * The first version of this spec only counted rows, and on an empty database
 * every count was 0, so `0 === 0` passed: the assertions were vacuous, and
 * the spec FAILED OUTRIGHT on any database that contained an invoice (the
 * invoice `status` came back as the ordinal 1, `StatusBadge` called
 * `.toLowerCase()` on it, the component threw, React unmounted the screen).
 *
 * So the journey seeds one purchase order, one goods receipt and one
 * supplier invoice through the product's own API (support.ts ->
 * seedProcurementDocuments), and then asserts on REAL data:
 *   * the seeded rows are VISIBLE in the corresponding screens,
 *   * the status badge of the seeded invoice shows the enum NAME
 *     ("PendingReconciliation"), which is the wire contract of
 *     OpsBridge.Api (OpsBridgeJsonContract),
 *   * the count each screen renders equals the number of rows the API reports,
 *   * and the counts are asserted to be GREATER THAN ZERO, so a `0 === 0`
 *     match can no longer pass.
 *
 * The identifiers are unique per run, so the spec is deterministic and can be
 * executed repeatedly against a database that already holds the rows of a
 * previous run.
 */
const SCREENS = [
  {
    path: '/dashboard',
    link: 'Operations Overview',
    heading: /Operations Overview/i,
  },
  {
    path: '/purchase-orders',
    link: 'Purchase Orders',
    heading: /Purchase Orders/i,
  },
  {
    path: '/goods-receipts',
    link: 'Goods Receipts',
    heading: /Goods Receipts/i,
  },
  {
    path: '/invoices',
    link: 'Supplier Invoices',
    heading: /Supplier Invoices/i,
  },
  {
    path: '/reconciliation',
    link: '3-Way Matching Workspace',
    heading: /3-Way Matching Workspace/i,
  },
  {
    path: '/discrepancies',
    link: 'Discrepancy Review',
    heading: /Discrepancy Investigation & Review/i,
  },
  {
    path: '/integrations',
    link: 'Integration Operations',
    heading: /Integration Operations & Event Mesh/i,
  },
  {
    path: '/audit',
    link: 'Audit Timeline',
    heading: /Audit & Compliance Timeline/i,
  },
] as const;

test('operator reaches all eight authenticated screens and each one renders live API data', async ({
  page,
}) => {
  // Eight screens, each of them a full page of API round trips.
  test.setTimeout(180_000);

  // ---- seed the data the screens must render -------------------------------
  // Written through the product's own API, so the rows exist in PostgreSQL
  // BEFORE the browser signs in: the app mounts its dashboard on the first
  // authenticated render, and that first metrics response is cached by
  // react-query - seeding afterwards would let the dashboard show the
  // pre-seed numbers.
  const seedToken = await apiLogin(page, 'Operator');
  const seeded = await seedProcurementDocuments(page, seedToken);

  await signInAs(page, 'Operator');

  // The API is the reference: the numbers the screens render are compared
  // against what the API reports, never against a hard-coded constant.
  const purchaseOrders = await apiGet<any[]>(page, '/api/v1/purchase-orders');
  const goodsReceipts = await apiGet<any[]>(page, '/api/v1/goods-receipts');
  const invoices = await apiGet<any[]>(page, '/api/v1/invoices');
  const discrepancies = await apiGet<any[]>(page, '/api/v1/reconciliation/discrepancies');
  const auditEvents = await apiGet<any[]>(page, '/api/v1/reconciliation/audit-trail');
  const dashboardMetrics = await apiGet<any>(page, '/api/v1/dashboard/metrics');

  // The guard against a vacuous `0 === 0`: the seeded documents MUST be in
  // the API's answer, and the answers must not be empty.
  expect(invoices.some((i) => i.invoiceNumber === seeded.invoiceNumber)).toBe(true);
  expect(purchaseOrders.some((p) => p.orderNumber === seeded.orderNumber)).toBe(true);
  expect(goodsReceipts.some((g) => g.receiptNumber === seeded.receiptNumber)).toBe(true);
  expect(invoices.length).toBeGreaterThan(0);
  expect(purchaseOrders.length).toBeGreaterThan(0);
  expect(goodsReceipts.length).toBeGreaterThan(0);
  // The dashboard counts EVERY invoice in the database, while the list
  // endpoint returns at most ONE PAGE of them (100 - see
  // ReconciliationEndpoints.cs, `invoiceRepo.ListAsync(100, ct)`). The two
  // are therefore equal only on a database that holds fewer than 100
  // invoices - which is exactly the "the CI job starts from an empty
  // database" accident a suite must not depend on: on any database that has
  // been used before, this assertion failed and the suite stopped being a
  // gate.
  //
  // What must hold on EVERY database: the dashboard's total is at least the
  // size of the page the API returned (the page is a subset of the total),
  // and the invoice this run seeded is in that page (asserted above).
  expect(dashboardMetrics.totalInvoices).toBeGreaterThanOrEqual(invoices.length);

  for (const screen of SCREENS) {
    const linkByHref = page.locator(`aside a[href="${screen.path}"]`);
    if ((await linkByHref.count()) > 0) {
      await linkByHref.first().click();
    } else {
      await page.getByRole('link', { name: screen.link }).click();
    }
    // The reconciliation workspace puts the selected invoice in the query
    // string (/reconciliation?invoiceId=...), so a query string is allowed.
    await expect(page).toHaveURL(new RegExp(`${screen.path}(\\?.*)?$`), { timeout: UI_TIMEOUT });

    // The page heading of every screen (h1, verified in each feature page).
    await expect(
      page.getByRole('heading', { name: screen.heading, level: 1 }),
    ).toBeVisible({ timeout: UI_TIMEOUT });
  }

  // ---- per-screen evidence that the API (and not a static shell) rendered ----

  // Dashboard: the four KPI cards are filled from GET /api/v1/dashboard/metrics.
  // While the query is in flight the cards show the '...' placeholder, so the
  // absence of that placeholder proves the response arrived. The "Total
  // Invoices" value is compared with the API's own number.
  await page.locator('aside a[href="/dashboard"]').first().click();
  await expect(page.getByText('Total Invoices', { exact: true })).toBeVisible();
  await expect(page.getByText('Matched & Approved', { exact: true })).toBeVisible();
  await expect(page.getByText('Open Discrepancies', { exact: true })).toBeVisible();
  await expect(page.getByText('Dead-Letter Queue', { exact: true })).toBeVisible();
  await expect(page.getByText('...', { exact: true })).toHaveCount(0, { timeout: UI_TIMEOUT });
  // The KPI value itself, not just the label: the value sits in the block
  // that follows the label row inside the KPI card. The dashboard refetches
  // its metrics every 10 s, so the value is polled instead of read once.
  await expect
    .poll(
      async () => {
        const testIdEl = page.locator('[data-testid="metric-total-invoices"]');
        if ((await testIdEl.count()) > 0) {
          return (await testIdEl.textContent())?.trim();
        }
        return (
          await page
            .getByText('Total Invoices', { exact: true })
            .locator('xpath=ancestor::div[contains(@class,"p-5")]//div[contains(@class,"text-2xl")]')
            .textContent()
        )?.trim();
      },
      { timeout: 20_000 },
    )
    .toBe(String(dashboardMetrics.totalInvoices));

  // Purchase orders: the counter is rendered from the API payload, and the
  // seeded order is one of the rendered rows.
  await page.locator('aside a[href="/purchase-orders"]').first().click();
  await expect(
    page.getByText(`Showing ${purchaseOrders.length} purchase orders`),
  ).toBeVisible({ timeout: UI_TIMEOUT });
  await expect(
    page.getByRole('cell', { name: seeded.orderNumber, exact: true }),
  ).toBeVisible({ timeout: UI_TIMEOUT });

  // Goods receipts: same pattern.
  await page.locator('aside a[href="/goods-receipts"]').first().click();
  await expect(
    page.getByText(`Showing ${goodsReceipts.length} warehouse receipts`),
  ).toBeVisible({ timeout: UI_TIMEOUT });
  await expect(
    page.getByRole('cell', { name: seeded.receiptNumber, exact: true }),
  ).toBeVisible({ timeout: UI_TIMEOUT });

  // Invoices: the directory title carries the number of invoices the API
  // returned, and the seeded invoice is rendered with its status.
  await page.locator('aside a[href="/invoices"]').first().click();
  await expect(
    page.getByRole('heading', { name: `Invoices Directory (${invoices.length})` }),
  ).toBeVisible({ timeout: UI_TIMEOUT });
  const seededInvoiceRow = page.getByRole('row', {
    name: new RegExp(escapeForRegExp(seeded.invoiceNumber)),
  });
  await expect(seededInvoiceRow).toBeVisible({ timeout: UI_TIMEOUT });
  // The status badge shows the enum NAME - the wire contract of the API.
  // (With the ordinal 1 the whole screen used to crash instead.)
  await expect(seededInvoiceRow.getByText('PendingReconciliation')).toBeVisible({
    timeout: UI_TIMEOUT,
  });

  // Reconciliation: the invoice selector is filled from GET /api/v1/invoices, so
  // the number of <option>s must equal the number of invoices the API reports,
  // and the selected option must name the seeded invoice and its status.
  await page.locator('aside a[href="/reconciliation"]').first().click();
  await expect(
    page.getByRole('heading', { name: /3-Way Matching Workspace/i, level: 1 }),
  ).toBeVisible();
  const invoiceSelect = page.getByRole('main').getByRole('combobox');
  await expect
    .poll(() => invoiceSelect.locator('option').count(), { timeout: UI_TIMEOUT })
    .toBe(invoices.length);
  await expect(invoiceSelect).toContainText(seeded.invoiceNumber);
  await expect(invoiceSelect).toContainText('PendingReconciliation');

  // Discrepancies: the log title carries the number of discrepancies.
  await page.locator('aside a[href="/discrepancies"]').first().click();
  await expect(
    page.getByRole('heading', { name: `Discrepancies Log (${discrepancies.length})` }),
  ).toBeVisible({ timeout: UI_TIMEOUT });

  // Integrations: the DLQ table and the ingestion stream come from the API.
  await page.locator('aside a[href="/integrations"]').first().click();
  await expect(
    page.getByRole('heading', { name: /Dead-Letter Queue \(DLQ\)/ }),
  ).toBeVisible({ timeout: UI_TIMEOUT });
  await expect(
    page.getByRole('heading', { name: 'Recent Ingested Events Stream' }),
  ).toBeVisible();

  // Audit: the timeline title carries the number of audit records.
  await page.locator('aside a[href="/audit"]').first().click();
  await expect(
    page.getByRole('heading', { name: `Audit Records (${auditEvents.length})` }),
  ).toBeVisible({ timeout: UI_TIMEOUT });
});

/** Escapes the characters that would otherwise be regex metacharacters. */
function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
