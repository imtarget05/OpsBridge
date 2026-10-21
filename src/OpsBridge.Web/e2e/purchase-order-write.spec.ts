import { expect, test } from '@playwright/test';
import { apiGet, signInAs, UI_TIMEOUT } from './support';

/**
 * Write path: a click in the browser -> POST /api/v1/purchase-orders -> a row in
 * PostgreSQL -> the same row read back through the API.
 *
 * The API endpoint requires the OperatorOrAdmin policy, so the Operator persona
 * is the one that may create a purchase order
 * (`src/OpsBridge.Api/Endpoints/ReconciliationEndpoints.cs`).
 *
 * The order number is unique per run so the spec can be executed repeatedly
 * against the same database without colliding with the row of a previous run.
 */
test('operator creates a purchase order in the browser and the row is persisted in PostgreSQL', async ({
  page,
}) => {
  test.setTimeout(180_000);

  const orderNumber = `PO-E2E-${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const quantity = 7;
  const unitPrice = 1234;

  await signInAs(page, 'Operator');
  await page.goto('/purchase-orders');

  await page.getByRole('button', { name: 'New Purchase Order' }).click();
  const modalTitle = page.getByRole('heading', { name: 'Create New Purchase Order' });
  await expect(modalTitle).toBeVisible({ timeout: UI_TIMEOUT });

  await page.getByLabel('PO Number', { exact: true }).fill(orderNumber);
  await page.getByLabel('Supplier Code', { exact: true }).fill('SUP-E2E-01');
  await page.getByLabel('Supplier Name', { exact: true }).fill('E2E Supplier');
  await page.getByLabel('SKU', { exact: true }).fill('SKU-E2E');
  await page.getByLabel('Description', { exact: true }).fill('E2E line item');
  await page.getByLabel('Quantity (PCS)', { exact: true }).fill(String(quantity));
  await page.getByLabel('Unit Price (VND)', { exact: true }).fill(String(unitPrice));

  await page.getByRole('button', { name: 'Create PO' }).click();

  // The create modal closes only on a successful mutation, and the list is
  // re-fetched from the API afterwards.
  await expect(modalTitle).toBeHidden({ timeout: UI_TIMEOUT });
  await expect(page.getByRole('cell', { name: orderNumber, exact: true })).toBeVisible({
    timeout: UI_TIMEOUT,
  });

  // ---- the row really is in the database: read it back through the API ----
  const persisted = await apiGet<any>(page, `/api/v1/purchase-orders/${orderNumber}`);
  expect(persisted.orderNumber).toBe(orderNumber);
  expect(persisted.supplierCode).toBe('SUP-E2E-01');
  expect(persisted.supplierName).toBe('E2E Supplier');
  expect(persisted.currency).toBe('VND');
  expect(persisted.lines).toHaveLength(1);
  expect(persisted.lines[0].itemSku).toBe('SKU-E2E');
  expect(persisted.lines[0].orderedQuantity).toBe(quantity);
  expect(persisted.lines[0].unitPrice).toBe(unitPrice);
  expect(persisted.totalAmount).toBe(quantity * unitPrice);

  // And it is part of the collection the UI renders.
  const allPurchaseOrders = await apiGet<any[]>(page, '/api/v1/purchase-orders');
  expect(allPurchaseOrders.map((po) => po.orderNumber)).toContain(orderNumber);
});
