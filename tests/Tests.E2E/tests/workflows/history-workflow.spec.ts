import { expect, test } from "@playwright/test";
import {
  closeHistoryPanel,
  expandFirstHistoryGroup,
  expandFirstSuccessHistorySection,
  openHistoryPanel,
} from "../../actions/history.actions";
import { authenticateAsManager } from "../../actions/auth.actions";
import { historyItems } from "../../fixtures/selectors";
import { cleanupTaskViaApi, createTaskViaApi, deleteTaskViaApi } from "../../support/task-api";
import { today } from "../../support/date";

test.describe("history workflows", () => {
  test.describe.configure({ mode: "serial" });

  let taskIdToCleanup: number | null = null;

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
    await page.goto(`/Schedule?view=day&date=${today()}`);
  });

  test.afterEach(async ({ page }) => {
    if (taskIdToCleanup !== null) {
      await cleanupTaskViaApi(page, taskIdToCleanup);
      taskIdToCleanup = null;
    }
  });

  test("creating a task via the API adds a success entry to the history panel", async ({ page }) => {
    await openHistoryPanel(page);
    const entriesBefore = await historyItems(page).count();

    await closeHistoryPanel(page);

    taskIdToCleanup = await createTaskViaApi(page, { title: "HistCreate" }, { slotIndex: 1 });

    await openHistoryPanel(page);
    await page.waitForTimeout(2_000);

    const entriesAfter = await historyItems(page).count();
    expect(entriesAfter).toBeGreaterThan(entriesBefore);
    expect(await page.locator(".schedule-history-item.outcome-success").count()).toBeGreaterThan(0);
  });

  test("deleting a task adds a success entry to the history panel", async ({ page }) => {
    taskIdToCleanup = await createTaskViaApi(page, { title: "HistDelete" }, { slotIndex: 4 });

    await openHistoryPanel(page);
    await page.waitForTimeout(500);
    const entriesBefore = await historyItems(page).count();

    await closeHistoryPanel(page);
    await deleteTaskViaApi(page, taskIdToCleanup);
    taskIdToCleanup = null;
    await page.waitForTimeout(1_000);

    await openHistoryPanel(page);
    await page.waitForTimeout(2_000);

    const entriesAfter = await historyItems(page).count();
    expect(entriesAfter).toBeGreaterThan(entriesBefore);
  });

  test("history entry shows actor name, action label, and outcome icon", async ({ page }) => {
    taskIdToCleanup = await createTaskViaApi(page, { title: "HistMeta" }, { slotIndex: 5 });
    await page.waitForTimeout(500);

    await openHistoryPanel(page);
    await expandFirstHistoryGroup(page);
    await expandFirstSuccessHistorySection(page);

    const firstEntry = page.locator(".schedule-history-item.outcome-success").first();
    await expect(firstEntry).toBeVisible({ timeout: 5_000 });
    await expect(firstEntry.locator(".schedule-history-item__meta")).not.toBeEmpty();
    await expect(firstEntry.locator(".schedule-history-item__action")).not.toBeEmpty();
    await expect(firstEntry.locator(".schedule-history-item__badge")).toBeVisible();
    await expect(firstEntry.locator(".schedule-history-item__message")).not.toBeEmpty();
  });

  test("rapid identical API calls produce only one history entry", async ({ page }) => {
    taskIdToCleanup = await createTaskViaApi(page, { title: "DedupTask" }, { slotIndex: 6 });
    await page.waitForTimeout(500);

    const stateResponse = await page.request.get("/api/plans/state");
    const state = stateResponse.ok() ? await stateResponse.json() : {};
    const planId = state.draftPlanId ?? state.activePlanId;
    test.skip(!planId, "No active or draft plan found.");

    const historyBefore = await page.request.get(`/api/plans/${planId}/history?limit=50`);
    const entriesBefore: unknown[] = historyBefore.ok() ? await historyBefore.json() : [];

    const updatePayload = {
      title: "DedupUpdate",
      version: 0,
      timezoneOffsetMinutes: 120,
    };
    await page.request.patch(`/api/tasks/${taskIdToCleanup}`, { data: updatePayload });
    await page.request.patch(`/api/tasks/${taskIdToCleanup}`, { data: { ...updatePayload, version: 1 } });
    await page.waitForTimeout(500);

    const historyAfter = await page.request.get(`/api/plans/${planId}/history?limit=50`);
    const entriesAfter: unknown[] = historyAfter.ok() ? await historyAfter.json() : [];

    expect(entriesAfter.length - entriesBefore.length).toBeLessThanOrEqual(1);
  });
});
