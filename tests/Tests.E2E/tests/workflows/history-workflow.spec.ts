import { expect, test } from "@playwright/test";
import { closeHistoryPanel } from "../../actions/history.actions";
import {
  expandFirstSuccessfulHistoryEntry,
  getCurrentPlanHistoryEntryCount,
  openManagerHistoryAfterTaskMutation,
  openManagerHistoryForToday,
} from "../../flows/history.flows";
import { createManagerTaskViaApi } from "../../flows/task-access.flows";
import { cleanupTaskViaApi, deleteTaskViaApi } from "../../support/task-api";

test.describe("history workflows", () => {
  test.describe.configure({ mode: "serial" });

  let taskIdToCleanup: number | null = null;

  test.beforeEach(async ({ page }) => {
    await openManagerHistoryForToday(page);
  });

  test.afterEach(async ({ page }) => {
    if (taskIdToCleanup !== null) {
      await cleanupTaskViaApi(page, taskIdToCleanup);
      taskIdToCleanup = null;
    }
  });

  test("creating a task via the API adds a success entry to the history panel", async ({ page }) => {
    await openManagerHistoryAfterTaskMutation(page, async () => {
      const result = await createManagerTaskViaApi(page, { title: "HistCreate" }, { slotIndex: 1 });
      taskIdToCleanup = result.taskId;
      return { targetDate: result.schedule.targetDate };
    });

    const firstEntry = await expandFirstSuccessfulHistoryEntry(page);
    await expect(firstEntry).toBeVisible({ timeout: 10_000 });
    expect(await page.locator(".schedule-history-item.outcome-success").count()).toBeGreaterThan(0);
  });

  test("deleting a task adds a success entry to the history panel", async ({ page }) => {
    const result = await createManagerTaskViaApi(page, { title: "HistDelete" }, { slotIndex: 4 });
    taskIdToCleanup = result.taskId;

    await openManagerHistoryAfterTaskMutation(page, async () => {
      await deleteTaskViaApi(page, taskIdToCleanup!);
      taskIdToCleanup = null;
      await page.waitForTimeout(1_000);
      return { targetDate: result.schedule.targetDate };
    });

    const firstEntry = await expandFirstSuccessfulHistoryEntry(page);
    await expect(firstEntry).toBeVisible({ timeout: 10_000 });
  });

  test("history entry shows actor name, action label, and outcome icon", async ({ page }) => {
    await openManagerHistoryAfterTaskMutation(page, async () => {
      const result = await createManagerTaskViaApi(page, { title: "HistMeta" }, { slotIndex: 5 });
      taskIdToCleanup = result.taskId;
      await page.waitForTimeout(500);
      return { targetDate: result.schedule.targetDate };
    });

    const firstEntry = await expandFirstSuccessfulHistoryEntry(page);
    await expect(firstEntry).toBeVisible({ timeout: 5_000 });
    await expect(firstEntry.locator(".schedule-history-item__meta")).not.toBeEmpty();
    await expect(firstEntry.locator(".schedule-history-item__action")).not.toBeEmpty();
    await expect(firstEntry.locator(".schedule-history-item__badge")).toBeVisible();
    await expect(firstEntry.locator(".schedule-history-item__message")).not.toBeEmpty();
  });

  test("rapid identical API calls produce only one history entry", async ({ page }) => {
    await closeHistoryPanel(page);
    const result = await createManagerTaskViaApi(page, { title: "DedupTask" }, { slotIndex: 6 });
    taskIdToCleanup = result.taskId;
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

