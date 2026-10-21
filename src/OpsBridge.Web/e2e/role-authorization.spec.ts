import { expect, test } from '@playwright/test';
import { signInAs, UI_TIMEOUT } from './support';

/**
 * Browser-level role check.
 *
 * WHAT THIS IS, AND WHAT IT IS NOT
 * This is a UI-GATING test: it proves that the web app renders different
 * controls for different personas. It is NOT a server-side authorization
 * test, and it must never be presented as one: the persona is asserted by
 * the CLIENT, because the demo login endpoint
 * (POST /api/v1/auth/login, mapped only in Development/Test -
 * `src/OpsBridge.Api/Endpoints/AuthEndpoints.cs`) takes the role from the
 * request body and puts it into the token. There is no identity provider
 * and no credential here. The server-side authorization of the same
 * policies is covered by the .NET suites
 * (tests/OpsBridge.IntegrationTests/OpsBridgeAuthBoundaryTests.cs and the
 * authorization guard tests), not by this file.
 *
 * The personas are genuinely different roles (OpsBridgeRoles), and the UI
 * renders different controls for them:
 *
 *   DiscrepanciesPage: `canReview = role === 'FinanceReviewer' || role === 'Admin'`
 *     -> a reviewer gets the review form, anybody else gets the read-only banner.
 *   IntegrationOpsPage: `canReplay = role === 'Admin' || role === 'Operator'`
 *     -> only Operator/Admin may replay a dead-lettered event.
 *
 * So the same two screens prove the opposite permissions for two personas:
 * Operator may replay but may not approve a variance, FinanceReviewer may
 * approve a variance but may not replay.
 *
 * The observable difference asserted here is the presence/absence of the
 * permission banner that the components render for the role that lacks the
 * permission (the action itself is not asserted, because a discrepancy /
 * dead-letter row only exists once the domain has such data).
 *
 * WHY THE ABSENCE ASSERTIONS WAIT FOR THE PAGE
 * ---------------------------------------------
 * "The banner is not there" is only evidence if the page HAS rendered. A
 * `toHaveCount(0)` assertion returns as soon as the count is zero - and the
 * count is zero while the page is still loading - so it passed for the wrong
 * reason: it could not distinguish "this role may not do it" from "the page
 * has not rendered yet". Every absence assertion below therefore waits for
 * the page's own heading first, and only then asserts that the banner is
 * absent. That makes the spec independent of the loading speed AND of any
 * pre-existing rows in the database: it asserts what the UI renders for a
 * role, and nothing else.
 */
test.describe('role-based permissions in the browser', () => {
  test('operator may replay dead letters but not approve variances; finance reviewer is the other way round', async ({
    page,
  }) => {
    test.setTimeout(180_000);

    const readOnlyBanner = page.getByText(/Read-Only: Switch to/);
    const replayLockBanner = page.getByText(/Operator or Admin role required to replay DLQ/);

    // The headings the two screens are identified by. Waiting for them is
    // what makes the absence assertions below meaningful.
    const discrepanciesHeading = page.getByRole('heading', {
      name: 'Discrepancy Investigation & Review',
      level: 1,
    });
    const integrationsHeading = page.getByRole('heading', {
      name: 'Integration Operations & Event Mesh',
      level: 1,
    });

    // ---------------------------------------------------------------- Operator
    await signInAs(page, 'Operator');
    // The navbar role switcher holds the role of the signed-in user.
    await expect(page.getByRole('banner').getByRole('combobox')).toHaveValue('Operator');

    // Operator lacks the Review permission -> the discrepancies screen is read-only.
    await page.goto('/discrepancies');
    await expect(discrepanciesHeading).toBeVisible({ timeout: UI_TIMEOUT });
    await expect(readOnlyBanner).toBeVisible({ timeout: UI_TIMEOUT });

    // Operator HAS the replay permission -> no lock on the integration screen.
    await page.goto('/integrations');
    await expect(integrationsHeading).toBeVisible({ timeout: UI_TIMEOUT });
    await expect(replayLockBanner).toHaveCount(0, { timeout: UI_TIMEOUT });

    // ------------------------------------------------------- FinanceReviewer
    await page.getByRole('button', { name: 'Log Out' }).click();
    await expect(page).toHaveURL(/\/login$/, { timeout: UI_TIMEOUT });

    await signInAs(page, 'FinanceReviewer');
    await expect(page.getByRole('banner').getByRole('combobox')).toHaveValue('FinanceReviewer');

    // FinanceReviewer holds the Review permission -> no read-only banner.
    await page.goto('/discrepancies');
    await expect(discrepanciesHeading).toBeVisible({ timeout: UI_TIMEOUT });
    await expect(readOnlyBanner).toHaveCount(0, { timeout: UI_TIMEOUT });

    // FinanceReviewer cannot replay dead letters -> the lock banner is shown.
    await page.goto('/integrations');
    await expect(integrationsHeading).toBeVisible({ timeout: UI_TIMEOUT });
    await expect(replayLockBanner).toBeVisible({ timeout: UI_TIMEOUT });
  });
});
