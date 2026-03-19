import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { createPlanBtn, draftBadge, previewBanner } from "../../fixtures/selectors";
import { createDraftViaApi, discardDraftIfExists } from "../../support/plan-api";
import { today } from "../../support/date";

test.describe("plan smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    await discardDraftIfExists(page);
  });

  test("manager sees the create plan button", async ({ page }) => {
    await discardDraftIfExists(page);
    await page.goto("/Schedule");

    await expect(createPlanBtn(page)).toBeVisible();
  });

  test("preview banner is visible when a draft exists", async ({ page }) => {
    await discardDraftIfExists(page);
    const draft = await createDraftViaApi(page);

    await page.goto(`/Schedule?view=week&date=${draft.startDate}`);
    await expect(previewBanner(page)).toBeVisible();
  });

  test("draft badge appears on active-plan dates when a draft exists", async ({ page }) => {
    await discardDraftIfExists(page);
    await createDraftViaApi(page);

    await page.goto(`/Schedule?view=day&date=${today()}`);
    await expect(draftBadge(page)).toBeVisible();
  });
});
