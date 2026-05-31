import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { backlogPanel, draftBadge, previewBanner } from "../../fixtures/selectors";
import {
  createDraftAndOpenDayView,
  createDraftAndOpenWeekPreview,
  openBacklogPlanningFlow,
  openManagerScheduleWithoutDraft,
} from "../../flows/plan.flows";
import { discardDraftIfExists } from "../../support/plan-api";

test.describe("plan smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    await discardDraftIfExists(page);
  });

  test("manager can access the backlog/templates planning panel", async ({ page }) => {
    await openManagerScheduleWithoutDraft(page);
    await openBacklogPlanningFlow(page);

    await expect(backlogPanel(page)).toBeVisible();
    await expect(page.locator("#taskDetailLabel")).toHaveText(/Darb/u);
  });

  test("preview banner is visible when a draft exists", async ({ page }) => {
    const draft = await createDraftAndOpenWeekPreview(page);

    await expect(previewBanner(page)).toBeVisible();
    expect(page.url()).toContain(draft.startDate);
    expect(page.url()).toContain("planScope=draft");
  });

  test("draft badge appears on active-plan dates when a draft exists", async ({ page }) => {
    const draft = await createDraftAndOpenDayView(page);

    await expect(draftBadge(page)).toBeVisible();
    expect(page.url()).toContain(draft.date);
  });
});
