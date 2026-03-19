import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { dragTaskHandleToTarget, resizeTaskRightHandleToSlot } from "../../actions/task-interactions.actions";
import { activateResizeMode } from "../../actions/task.actions";
import { taskResizeOverlay, taskResizeRightHandle } from "../../fixtures/selectors";
import { discardDraftIfExists } from "../../support/plan-api";
import {
  cleanupTaskViaApi,
  createTaskViaApi,
  getAnotherVisibleSlotOnSameTechnician,
  getNextVisibleSlotOnSameTechnician,
  getTaskScheduleContext,
  getVisibleScheduleSlot,
} from "../../support/task-api";
import { openDraftDaySchedule } from "../../flows/task.flows";

test.describe("task interaction workflows", () => {
  test.describe.configure({ mode: "serial" });

  const taskIdsToCleanup: number[] = [];

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of taskIdsToCleanup) {
      await cleanupTaskViaApi(page, id);
    }
    taskIdsToCleanup.length = 0;
    await discardDraftIfExists(page);
  });

  test("moves a task to a different time slot on the same technician via drag", async ({ page }) => {
    const date = await openDraftDaySchedule(page);
    const sourceSlot = await getVisibleScheduleSlot(page, { slotIndex: 3 });
    const taskId = await createTaskViaApi(page, { title: "MoveTask", ...sourceSlot });
    taskIdsToCleanup.push(taskId);

    await page.goto(`/Schedule?view=day&date=${date}`);
    await page.waitForLoadState("networkidle");

    const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
    await expect(card).toBeVisible({ timeout: 10_000 });

    const currentTask = await getTaskScheduleContext(page, taskId);
    const targetSlot = await getAnotherVisibleSlotOnSameTechnician(page, currentTask);
    test.skip(!targetSlot, "No other empty slot available for the same technician after seeding.");

    const targetSlotLocator = page.locator(
      `.schedule-slot[data-tech-id="${targetSlot!.technicianId}"][data-date="${targetSlot!.targetDate}"][data-slot-start-minutes="${targetSlot!.startMinutes}"]`
    ).first();
    await expect(targetSlotLocator).toBeVisible({ timeout: 10_000 });

    await dragTaskHandleToTarget(page, card, targetSlotLocator);

    const updated = await getTaskScheduleContext(page, taskId);
    expect(updated.technicianId).toBe(targetSlot!.technicianId);
    expect(updated.targetDate).toBe(targetSlot!.targetDate);
    expect(updated.startMinutes).toBe(targetSlot!.startMinutes);
    expect(updated.endMinutes).toBe(targetSlot!.endMinutes);
  });

  test("moves a task to a different technician row via drag", async ({ page }) => {
    const date = await openDraftDaySchedule(page);
    const sourceSlot = await getVisibleScheduleSlot(page, { slotIndex: 0 });
    const taskId = await createTaskViaApi(page, { title: "CrossTechMove", ...sourceSlot });
    taskIdsToCleanup.push(taskId);

    await page.goto(`/Schedule?view=day&date=${date}`);
    await page.waitForLoadState("networkidle");

    const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
    await expect(card).toBeVisible({ timeout: 10_000 });

    const allSlotData = await page.locator(".schedule-slot[data-tech-id][data-date][data-slot-start-minutes]").evaluateAll((els) =>
      els.map((el) => ({
        techId: el.getAttribute("data-tech-id") ?? "",
        targetDate: el.getAttribute("data-date") ?? "",
        startMinutes: Number(el.getAttribute("data-slot-start-minutes") ?? "0"),
      }))
    );
    const differentTechSlot = allSlotData.find(
      (slot) => slot.techId !== String(sourceSlot.technicianId) && slot.targetDate === sourceSlot.targetDate
    );
    test.skip(!differentTechSlot, "No slots for a different technician found in the rendered schedule.");

    const targetSlot = page
      .locator(
        `.schedule-slot[data-tech-id="${differentTechSlot!.techId}"][data-date="${differentTechSlot!.targetDate}"][data-slot-start-minutes="${differentTechSlot!.startMinutes}"]`
      )
      .first();

    await dragTaskHandleToTarget(page, card, targetSlot);

    const updated = await getTaskScheduleContext(page, taskId);
    expect(updated.technicianId).toBe(Number(differentTechSlot!.techId));
    expect(updated.targetDate).toBe(String(differentTechSlot!.targetDate));
    expect(updated.endMinutes - updated.startMinutes).toBe(sourceSlot.endMinutes - sourceSlot.startMinutes);
  });

  test("swaps two tasks on the same technician via drag", async ({ page }) => {
    const date = await openDraftDaySchedule(page);

    const slotA = await getVisibleScheduleSlot(page, { slotIndex: 1 });
    const taskIdA = await createTaskViaApi(page, { title: "SwapA", ...slotA });
    taskIdsToCleanup.push(taskIdA);

    await page.goto(`/Schedule?view=day&date=${date}`);
    await page.waitForLoadState("networkidle");

    const actualSlotA = await getTaskScheduleContext(page, taskIdA);
    const slotB = await getAnotherVisibleSlotOnSameTechnician(page, actualSlotA);
    test.skip(!slotB, "No second empty slot available on the same technician after seeding the first task.");

    const taskIdB = await createTaskViaApi(page, { title: "SwapB", ...slotB! });
    taskIdsToCleanup.push(taskIdB);

    await page.goto(`/Schedule?view=day&date=${date}`);
    await page.waitForLoadState("networkidle");

    const beforeSwapA = await getTaskScheduleContext(page, taskIdA);
    const beforeSwapB = await getTaskScheduleContext(page, taskIdB);

    const cardA = page.locator(`.task-card[data-task-id="${taskIdA}"]`);
    const cardB = page.locator(`.task-card[data-task-id="${taskIdB}"]`);
    await expect(cardA).toBeVisible({ timeout: 10_000 });
    await expect(cardB).toBeVisible({ timeout: 10_000 });

    await dragTaskHandleToTarget(page, cardA, cardB);

    const updatedA = await getTaskScheduleContext(page, taskIdA);
    const updatedB = await getTaskScheduleContext(page, taskIdB);
    expect(updatedA.startMinutes).toBe(beforeSwapB.startMinutes);
    expect(updatedB.startMinutes).toBe(beforeSwapA.startMinutes);

    await expect(page.locator(`.task-card[data-task-id="${taskIdA}"]`)).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(`.task-card[data-task-id="${taskIdB}"]`)).toBeVisible({ timeout: 10_000 });
  });

  test("extends a task end time via the resize handle", async ({ page }) => {
    const date = await openDraftDaySchedule(page);
    const slot = await getVisibleScheduleSlot(page, { slotIndex: 0 });
    const taskId = await createTaskViaApi(page, { title: "ResizeTask", ...slot });
    taskIdsToCleanup.push(taskId);

    await page.goto(`/Schedule?view=day&date=${date}`);
    await page.waitForLoadState("networkidle");

    const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
    await expect(card).toBeVisible({ timeout: 10_000 });

    await activateResizeMode(card);
    await expect(taskResizeOverlay(page)).toBeVisible({ timeout: 5_000 });

    const currentTask = await getTaskScheduleContext(page, taskId);
    const nextSlot = await getNextVisibleSlotOnSameTechnician(page, currentTask);
    test.skip(!nextSlot, "No adjacent empty slot found to extend the task into after seeding.");

    const rightHandle = taskResizeRightHandle(page);
    const nextSlotLocator = page.locator(
      `.schedule-slot[data-tech-id="${nextSlot!.technicianId}"][data-date="${nextSlot!.targetDate}"][data-slot-start-minutes="${nextSlot!.startMinutes}"]`
    ).first();
    await expect(nextSlotLocator).toBeVisible({ timeout: 10_000 });

    await resizeTaskRightHandleToSlot(page, rightHandle, nextSlotLocator);

    const updated = await getTaskScheduleContext(page, taskId);
    expect(updated.endMinutes).toBe(nextSlot!.endMinutes);

  });
});
