import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { discardPlanBtn, draftBadge, previewBanner, publishPlanBtn } from "../../fixtures/selectors";
import { discardDraftIfExists } from "../../support/plan-api";
import { today } from "../../support/date";
import {
  createDraftThroughUi,
  discardDraftThroughUi,
  openDraftWeekPreview,
  publishDraftThroughUi,
} from "../../flows/plan.flows";

test.describe("plan lifecycle workflows", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    await discardDraftIfExists(page);
  });

  test("creates a draft plan through the UI and shows the preview banner", async ({ page }) => {
    await discardDraftIfExists(page);
    await page.goto("/Schedule");

    const draft = await createDraftThroughUi(page);

    await page.waitForURL(/view=week/i, { timeout: 20_000 });
    await expect(previewBanner(page)).toBeVisible();
    expect(page.url()).toContain(draft.startDate);
  });

  test("publishes a draft plan: preview banner disappears and no draft badge remains", async ({ page }) => {
    await discardDraftIfExists(page);
    const draft = await openDraftWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    await expect(publishPlanBtn(page)).toBeVisible();

    await publishDraftThroughUi(page);
    await page.waitForURL(/Schedule/i, { timeout: 20_000 });

    await expect(previewBanner(page)).not.toBeVisible();
    await page.goto(`/Schedule?view=day&date=${draft.startDate}`);
    await expect(draftBadge(page)).not.toBeVisible();
  });

  test("discards a draft plan: preview banner and draft badge both disappear", async ({ page }) => {
    await discardDraftIfExists(page);
    await openDraftWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    await expect(discardPlanBtn(page)).toBeVisible();

    await discardDraftThroughUi(page);
    await page.waitForURL(/Schedule/i, { timeout: 20_000 });

    await expect(previewBanner(page)).not.toBeVisible();
    await page.goto(`/Schedule?view=day&date=${today()}`);
    await expect(draftBadge(page)).not.toBeVisible();
  });

  test("creating a second draft via the API is rejected while one already exists", async ({ page }) => {
    await discardDraftIfExists(page);
    await openDraftWeekPreview(page);

    const response = await page.request.post("/api/plans/draft", {
      data: { weeks: 2 },
    });

    expect([400, 409, 422]).toContain(response.status());
  });
});
