import { expect, type Page } from '@playwright/test';

/**
 * Shared helpers for the OpsBridge browser journey.
 *
 * Everything here talks to the REAL stack: the Vite dev server proxies `/api` to
 * `OpsBridge.Api` (see vite.config.ts) and the API reads/writes PostgreSQL. No
 * request is intercepted or mocked.
 */

/**
 * The four quick-persona buttons rendered by
 * `src/features/auth/LoginPage.tsx`:
 *
 *   Finance Reviewer -> alice_finance / FinanceReviewer
 *   Operator          -> bob_operator  / Operator
 *   Administrator     -> admin_system  / Admin
 *   Auditor           -> compliance_auditor / Auditor
 *
 * The demo login endpoint only exists in the Development/Test environment
 * (`src/OpsBridge.Api/Endpoints/AuthEndpoints.cs`), so the CI job runs the API
 * with ASPNETCORE_ENVIRONMENT=Development.
 */
export const PERSONAS = {
  FinanceReviewer: 'Finance Reviewer',
  Operator: 'Operator',
  Admin: 'Administrator',
  Auditor: 'Auditor',
} as const;

export type PersonaName = keyof typeof PERSONAS;

/** Generous timeouts: the CI runner is shared and the API may be briefly busy. */
export const UI_TIMEOUT = 30_000;

/**
 * Signs in through the login page with one of the quick-persona buttons and
 * waits until the app has actually left the login screen.
 *
 * The app redirects to the location it remembers (`location.state.from`) or to
 * /dashboard when there is none, so this helper asserts "the session is
 * authenticated" instead of a hard-coded target URL.
 */
export async function signInAs(page: Page, persona: PersonaName): Promise<void> {
  await page.goto('/login');
  await page.getByRole('button', { name: PERSONAS[persona], exact: true }).click();
  await expect(page).not.toHaveURL(/\/login$/, { timeout: UI_TIMEOUT });
  // The navbar (and with it the role switcher) only renders for an
  // authenticated session.
  await expect(page.getByRole('banner').getByRole('combobox')).toBeVisible({
    timeout: UI_TIMEOUT,
  });
}

/** Reads the JWT the web app stored after a successful login. */
export async function authToken(page: Page): Promise<string> {
  const token = await page.evaluate(() => localStorage.getItem('opsbridge_token'));
  if (!token) {
    throw new Error('No auth token in localStorage - the user is not signed in.');
  }
  return token;
}

/**
 * Calls the real API through the same origin the browser uses (the Vite dev
 * server proxies /api to the API on :5000) and returns the parsed body.
 */
export async function apiGet<T = any>(page: Page, path: string): Promise<T> {
  const token = await authToken(page);
  const response = await page.request.get(path, {
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.ok(), `GET ${path} answered ${response.status()}`).toBeTruthy();
  return (await response.json()) as T;
}

/**
 * Writes through the real API, exactly like the browser does: same origin
 * (so the request goes through the Vite proxy), same bearer token, same
 * JSON body. No request is intercepted or mocked.
 */
export async function apiPost<T = any>(
  page: Page,
  path: string,
  body: unknown,
  token?: string,
): Promise<T> {
  const bearer = token ?? (await authToken(page));
  const response = await page.request.post(path, {
    headers: {
      Authorization: `Bearer ${bearer}`,
      'Content-Type': 'application/json',
    },
    data: body,
  });
  expect(response.ok(), `POST ${path} answered ${response.status()}`).toBeTruthy();
  return (await response.json()) as T;
}

/**
 * Signs in through the API (the Development-only demo endpoint
 * `POST /api/v1/auth/login`) and returns the access token.
 *
 * Used to seed data BEFORE the browser journey starts: the app mounts its
 * dashboard as soon as the user is signed in, and that first metrics fetch is
 * cached by react-query - seeding afterwards would let the dashboard show the
 * pre-seed numbers for up to `staleTime` (5 s).
 */
export async function apiLogin(page: Page, persona: PersonaName): Promise<string> {
  const response = await page.request.post('/api/v1/auth/login', {
    headers: { 'Content-Type': 'application/json' },
    data: { username: `e2e_${persona.toLowerCase()}`, role: persona },
  });
  expect(response.ok(), `POST /api/v1/auth/login answered ${response.status()}`).toBeTruthy();
  const body = (await response.json()) as { accessToken: string };
  expect(body.accessToken, 'the demo login response carried no accessToken').toBeTruthy();
  return body.accessToken;
}

/** The three procurement documents the smoke journey seeds. */
export interface SeededDocuments {
  orderNumber: string;
  receiptNumber: string;
  invoiceNumber: string;
  supplierCode: string;
  sku: string;
}

/**
 * Seeds one purchase order, one goods receipt and one supplier invoice
 * through the product's own API - the same write path the UI uses
 * (`POST /api/v1/purchase-orders`, `POST /api/v1/goods-receipts`,
 * `POST /api/v1/invoices`), only driven directly instead of through the
 * forms.
 *
 * The identifiers are unique per run, so the helper can be executed repeatedly
 * against a database that already holds the rows of a previous run: nothing
 * collides and nothing has to be cleaned up first.
 *
 * The invoice is created with the status the API gives a new invoice
 * (`PendingReconciliation`), so a UI that reads the status as a NAME (the
 * contract the API documents in OpsBridgeJsonContract) selects it, and a UI
 * that receives the ordinal instead shows a broken screen.
 */
export async function seedProcurementDocuments(
  page: Page,
  token: string,
): Promise<SeededDocuments> {
  const unique = `${Date.now()}-${Math.floor(Math.random() * 100000)}`;
  const orderNumber = `PO-E2E-${unique}`;
  const receiptNumber = `GR-E2E-${unique}`;
  const invoiceNumber = `INV-E2E-${unique}`;
  const supplierCode = 'SUP-E2E';
  const sku = 'SKU-E2E';

  await apiPost(
    page,
    '/api/v1/purchase-orders',
    {
      orderNumber,
      supplierCode,
      supplierName: 'E2E Supplier',
      currency: 'VND',
      sourceSystem: 'BrowserJourney',
      lines: [
        {
          lineNumber: 1,
          itemSku: sku,
          description: 'E2E line item',
          orderedQuantity: 10,
          unitOfMeasure: 'PCS',
          unitPrice: 1000,
        },
      ],
    },
    token,
  );

  await apiPost(
    page,
    '/api/v1/goods-receipts',
    {
      receiptNumber,
      purchaseOrderNumber: orderNumber,
      supplierCode,
      sourceSystem: 'BrowserJourney',
      lines: [{ lineNumber: 1, itemSku: sku, receivedQuantity: 10, unitOfMeasure: 'PCS' }],
    },
    token,
  );

  await apiPost(
    page,
    '/api/v1/invoices',
    {
      invoiceNumber,
      purchaseOrderNumber: orderNumber,
      supplierCode,
      supplierName: 'E2E Supplier',
      currency: 'VND',
      sourceSystem: 'BrowserJourney',
      lines: [
        {
          lineNumber: 1,
          itemSku: sku,
          description: 'E2E line item',
          invoicedQuantity: 10,
          unitOfMeasure: 'PCS',
          unitPrice: 1000,
          taxRate: 0.1,
        },
      ],
    },
    token,
  );

  return { orderNumber, receiptNumber, invoiceNumber, supplierCode, sku };
}
