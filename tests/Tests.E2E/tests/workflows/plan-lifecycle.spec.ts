import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { discardPlanBtn, draftBadge, previewBanner, publishPlanBtn } from "../../fixtures/selectors";
import {
  createDraftAndOpenWeekPreview,
  createDraftThroughUi,
  discardCurrentDraftAndWaitForSchedule,
  openManagerScheduleWithoutDraft,
  publishCurrentDraftAndWaitForSchedule,
} from "../../flows/plan.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { today } from "../../support/date";

test.describe("plan lifecycle workflows", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    await discardDraftIfExists(page);
  });

  test("creates a draft plan through the UI and shows the preview banner", async ({ page }) => {
    await openManagerScheduleWithoutDraft(page);

    const draft = await createDraftThroughUi(page);

    await page.waitForURL(/view=week/i, { timeout: 20_000 });
    await expect(previewBanner(page)).toBeVisible();
    expect(page.url()).toContain(draft.startDate);
  });

  test("publishes a draft plan: preview banner disappears and no draft badge remains", async ({ page }) => {
    const draft = await createDraftAndOpenWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    await expect(publishPlanBtn(page)).toBeVisible();

    await publishCurrentDraftAndWaitForSchedule(page);

    await expect(previewBanner(page)).not.toBeVisible();
    await page.goto(`/Schedule?view=day&date=${draft.startDate}`);
    await expect(draftBadge(page)).not.toBeVisible();
  });

  test("discards a draft plan: preview banner and draft badge both disappear", async ({ page }) => {
    await createDraftAndOpenWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    await expect(discardPlanBtn(page)).toBeVisible();

    await discardCurrentDraftAndWaitForSchedule(page);

    await expect(previewBanner(page)).not.toBeVisible();
    await page.goto(`/Schedule?view=day&date=${today()}`);
    await expect(draftBadge(page)).not.toBeVisible();
  });

  test("creating a second draft via the API is rejected while one already exists", async ({ page }) => {
    await createDraftAndOpenWeekPreview(page);

    const response = await page.request.post("/api/plans/draft", {
      data: { weeks: 2 },
    });

    expect([400, 409, 422]).toContain(response.status());
  });
});