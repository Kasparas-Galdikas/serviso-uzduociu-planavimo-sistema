import { expect, test, type Page } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { backlogBulkScheduleBtn, discardPlanBtn, draftBadge, previewBanner } from "../../fixtures/selectors";
import {
  createDraftAndOpenWeekPreview,
  discardCurrentDraftAndWaitForSchedule,
  finishAutomaticDraftPlanning,
  openManagerScheduleWithoutDraft,
  openBacklogPlanningFlow,
  startAutomaticDraftPlanningFromBacklog,
} from "../../flows/plan.flows";
import { cleanupBacklogItemViaApi, createBacklogItemViaApi } from "../../support/backlog-api";
import { discardDraftIfExists } from "../../support/plan-api";
import { today } from "../../support/date";

test.describe("plan lifecycle workflows", () => {
  test.describe.configure({ mode: "serial" });

  const backlogIdsToCleanup: number[] = [];

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    for (const backlogId of backlogIdsToCleanup) {
      await cleanupBacklogItemViaApi(page, backlogId);
    }

    backlogIdsToCleanup.length = 0;
    await discardDraftIfExists(page);
  });

  async function seedBacklogItem(page: Page, titlePrefix: string) {
    const backlogItemId = await createBacklogItemViaApi(page, {
      title: `${titlePrefix}-${Date.now().toString().slice(-6)}`,
      instructions: `${titlePrefix} instructions`,
    });
    backlogIdsToCleanup.push(backlogItemId);
    return backlogItemId;
  }

  async function selectBacklogItemForPlanning(page: Page, backlogItemId: number) {
    const backlogCard = page.locator(`.backlog-card[data-backlog-id="${backlogItemId}"]`).first();
    await expect(backlogCard).toBeVisible({ timeout: 10_000 });
    await backlogCard.click();
    await expect(backlogBulkScheduleBtn(page)).toBeEnabled({ timeout: 10_000 });
  }

  test("starts draft planning from backlog and shows the preview banner", async ({ page }) => {
    const backlogItemId = await seedBacklogItem(page, "plan-draft");
    await openManagerScheduleWithoutDraft(page);
    await openBacklogPlanningFlow(page);
    await selectBacklogItemForPlanning(page, backlogItemId);
    await startAutomaticDraftPlanningFromBacklog(page);

    await page.waitForURL(/planScope=draft/i, { timeout: 20_000 });
    await expect(previewBanner(page)).toBeVisible();
    expect(page.url()).toContain("planScope=draft");
  });

  test("finishes automatic draft planning: draft mode exits and no draft badge remains", async ({ page }) => {
    const backlogItemId = await seedBacklogItem(page, "plan-publish");
    await openManagerScheduleWithoutDraft(page);
    await openBacklogPlanningFlow(page);
    await selectBacklogItemForPlanning(page, backlogItemId);
    await startAutomaticDraftPlanningFromBacklog(page);

    await expect(previewBanner(page)).toBeVisible({ timeout: 20_000 });
    await finishAutomaticDraftPlanning(page);

    await expect(previewBanner(page)).not.toBeVisible();
    await expect(page).not.toHaveURL(/planScope=draft/i);
    await page.goto(`/Schedule?view=day&date=${today()}`);
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

  test("ensuring a draft via the API refreshes the current draft instead of rejecting it", async ({ page }) => {
    const draft = await createDraftAndOpenWeekPreview(page);
    const requestedStartDate = today();

    const response = await page.request.post("/api/plans/backlog-draft/ensure", {
      data: {
        startDate: requestedStartDate,
        endDate: draft.endDate,
        weeks: 2,
      },
    });

    expect(response.ok()).toBeTruthy();
    const body = await response.json().catch(() => null);
    expect(String(body?.periodStart ?? "")).toBeTruthy();
    expect(String(body?.periodEnd ?? "")).toBe(draft.endDate);

    const stateResponse = await page.request.get("/api/plans/state");
    expect(stateResponse.ok()).toBeTruthy();
    const state = await stateResponse.json();
    expect(Number(state?.draftPlanId ?? 0)).toBeGreaterThan(0);
  });
});
