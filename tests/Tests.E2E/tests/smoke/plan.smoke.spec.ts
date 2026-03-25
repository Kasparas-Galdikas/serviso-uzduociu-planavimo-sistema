import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { createPlanBtn, draftBadge, previewBanner } from "../../fixtures/selectors";
import {
  createDraftAndOpenDayView,
  createDraftAndOpenWeekPreview,
  openManagerScheduleWithoutDraft,
} from "../../flows/plan.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { today } from "../../support/date";

test.describe("plan smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    await discardDraftIfExists(page);
  });

  test("manager sees the create plan button", async ({ page }) => {
    await openManagerScheduleWithoutDraft(page);

    await expect(createPlanBtn(page)).toBeVisible();
  });

  test("preview banner is visible when a draft exists", async ({ page }) => {
    const draft = await createDraftAndOpenWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    expect(page.url()).toContain(draft.startDate);
  });

  test("draft badge appears on active-plan dates when a draft exists", async ({ page }) => {
    await createDraftAndOpenDayView(page, today());

    await expect(draftBadge(page)).toBeVisible();
  });
});