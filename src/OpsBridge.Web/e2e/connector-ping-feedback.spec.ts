import { expect, test } from '@playwright/test';

test('connector ping failure is shown as a failure, not a successful connection', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('opsbridge_token', 'test-token'));
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') {
      await route.fulfill({ json: { id: 'operator', username: 'operator', email: 'operator@example.test', role: 'Operator' } });
    } else if (path === '/api/v1/connectors') {
      await route.fulfill({ json: [{ id: 'conn-factoryflow', name: 'FactoryFlow REST Connector', baseUrl: 'https://factoryflow.example.test/api' }] });
    } else if (path === '/api/v1/connectors/conn-factoryflow/ping') {
      await route.fulfill({ json: { success: false, latencyMs: 0, message: 'FactoryFlow unavailable', targetUrl: 'https://factoryflow.example.test/api' } });
    } else {
      await route.fulfill({ json: [] });
    }
  });

  await page.goto('/integrations');
  await page.getByRole('button', { name: 'Kiểm tra kết nối (Test Ping Cloud)' }).click();

  await expect(page.getByText('FactoryFlow unavailable')).toBeVisible();
  await expect(page.getByText(/200 OK · Phản hồi/)).toHaveCount(0);
  await expect(page.getByText('TLS 1.3 Verified')).toHaveCount(0);
});

for (const [status, message] of [[502, 'Connector returned HTTP 503.'], [504, 'Connector health check timed out.']] as const) {
  test(`connector ping HTTP ${status} shows its API error`, async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('opsbridge_token', 'test-token'));
    await page.route('**/api/v1/**', async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/api/v1/auth/me') {
        await route.fulfill({ json: { id: 'operator', username: 'operator', email: 'operator@example.test', role: 'Operator' } });
      } else if (path === '/api/v1/connectors') {
        await route.fulfill({ json: [{ id: 'conn-factoryflow', name: 'FactoryFlow REST Connector', baseUrl: 'https://factoryflow.example.test/api' }] });
      } else if (path === '/api/v1/connectors/conn-factoryflow/ping') {
        await route.fulfill({ status, json: { message } });
      } else {
        await route.fulfill({ json: [] });
      }
    });

    await page.goto('/integrations');
    await page.getByRole('button', { name: 'Kiểm tra kết nối (Test Ping Cloud)' }).click();

    await expect(page.getByRole('status')).toContainText(message);
    await expect(page.getByText(/200 OK · Phản hồi/)).toHaveCount(0);
    await expect(page.getByText('TLS 1.3 Verified')).toHaveCount(0);
  });
}
