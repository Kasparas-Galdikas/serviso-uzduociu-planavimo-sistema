import { expect, test } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import { dragTaskHandleToTarget, resizeTaskRightHandleToSlot } from "../../actions/task-interactions.actions";
import { activateResizeMode, saveTask, waitForTaskPanel } from "../../actions/task.actions";
import { taskInstructionsInput, taskPrioritySelect, taskResizeOverlay, taskResizeRightHandle, taskSkillSelect, taskStartInput, taskTitleInput } from "../../fixtures/selectors";
import { createDraftTaskForInteraction } from "../../flows/task.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { cleanupBacklogItemViaApi, createBacklogItemViaApi } from "../../support/backlog-api";
import {
  cleanupTaskViaApi,
  createTaskViaApi,
  getAnotherVisibleSlotOnSameTechnician,
  getNextVisibleSlotOnSameTechnician,
  getTaskDetailViaApi,
  getTaskScheduleContext,
  getVisibleScheduleSlot,
} from "../../support/task-api";

test.describe("task interaction workflows", () => {
  test.describe.configure({ mode: "serial" });

  const taskIdsToCleanup: number[] = [];
  const backlogIdsToCleanup: number[] = [];

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    for (const id of taskIdsToCleanup) {
      await cleanupTaskViaApi(page, id);
    }
    taskIdsToCleanup.length = 0;
    for (const id of backlogIdsToCleanup) {
      await cleanupBacklogItemViaApi(page, id);
    }
    backlogIdsToCleanup.length = 0;
    await discardDraftIfExists(page);
  });

  test("moves a task to a different time slot on the same technician via drag", async ({ page }) => {
    const sourceSlot = await getVisibleScheduleSlot(page, { slotIndex: 3 }).catch(() => null);
    test.skip(!sourceSlot, "No visible schedule slot available for interaction seeding.");

    const { taskId, card } = await createDraftTaskForInteraction(page, { title: "MoveTask", ...sourceSlot! });
    taskIdsToCleanup.push(taskId);

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
    const sourceSlot = await getVisibleScheduleSlot(page, { slotIndex: 0 }).catch(() => null);
    test.skip(!sourceSlot, "No visible schedule slot available for interaction seeding.");

    const { taskId, card } = await createDraftTaskForInteraction(page, { title: "CrossTechMove", ...sourceSlot! });
    taskIdsToCleanup.push(taskId);

    await expect(card).toBeVisible({ timeout: 10_000 });

    const allSlotData = await page.locator(".schedule-slot[data-tech-id][data-date][data-slot-start-minutes]").evaluateAll((els) =>
      els.map((el) => ({
        techId: el.getAttribute("data-tech-id") ?? "",
        targetDate: el.getAttribute("data-date") ?? "",
        startMinutes: Number(el.getAttribute("data-slot-start-minutes") ?? "0"),
      }))
    );
    const differentTechSlot = allSlotData.find(
      (slot) => slot.techId !== String(sourceSlot!.technicianId) && slot.targetDate === sourceSlot!.targetDate
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
    expect(updated.endMinutes - updated.startMinutes).toBe(sourceSlot!.endMinutes - sourceSlot!.startMinutes);
  });

  test("swaps two tasks on the same technician via drag", async ({ page }) => {
    const slotA = await getVisibleScheduleSlot(page, { slotIndex: 1 }).catch(() => null);
    test.skip(!slotA, "No visible schedule slot available for interaction seeding.");

    const firstTask = await createDraftTaskForInteraction(page, { title: "SwapA", ...slotA! });
    taskIdsToCleanup.push(firstTask.taskId);

    const beforeSwapA = await getTaskScheduleContext(page, firstTask.taskId);
    const slotB = await getAnotherVisibleSlotOnSameTechnician(page, beforeSwapA);
    test.skip(!slotB, "No second empty slot available on the same technician after seeding the first task.");

    const secondTask = await createDraftTaskForInteraction(page, { title: "SwapB", ...slotB! }, { date: firstTask.date });
    taskIdsToCleanup.push(secondTask.taskId);

    const beforeSwapB = await getTaskScheduleContext(page, secondTask.taskId);

    await expect(firstTask.card).toBeVisible({ timeout: 10_000 });
    await expect(secondTask.card).toBeVisible({ timeout: 10_000 });

    await dragTaskHandleToTarget(page, firstTask.card, secondTask.card);

    const updatedA = await getTaskScheduleContext(page, firstTask.taskId);
    const updatedB = await getTaskScheduleContext(page, secondTask.taskId);
    expect(updatedA.startMinutes).toBe(beforeSwapB.startMinutes);
    expect(updatedB.startMinutes).toBe(beforeSwapA.startMinutes);

    await expect(page.locator(`.task-card[data-task-id="${firstTask.taskId}"]`)).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(`.task-card[data-task-id="${secondTask.taskId}"]`)).toBeVisible({ timeout: 10_000 });
  });

  test("extends a task end time via the resize handle", async ({ page }) => {
    const slot = await getVisibleScheduleSlot(page, { slotIndex: 0 }).catch(() => null);
    test.skip(!slot, "No visible schedule slot available for interaction seeding.");

    const { taskId, card } = await createDraftTaskForInteraction(page, { title: "ResizeTask", ...slot! });
    taskIdsToCleanup.push(taskId);

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

  test("preloads dragged backlog template data into the dropped schedule slot create form", async ({ page }) => {
    const slot = await getVisibleScheduleSlot(page, { slotIndex: 2 }).catch(() => null);
    test.skip(!slot, "No visible schedule slot available for backlog drag-drop.");

    const backlogItemId = await createBacklogItemViaApi(page, {
      title: "Drag backlog template",
      priority: 2,
      itemType: 1,
      skillLevel: "Advanced",
      objectName: "Vilniaus prekybos centras",
      objectAddress: "Konstitucijos pr. 16, Vilnius",
      instructions: "Drag backlog instructions",
    });
    backlogIdsToCleanup.push(backlogItemId);

    await page.evaluate(() => (window as Window & { schedule?: any }).schedule?.backlog?.openBacklogPanel?.());

    const backlogCard = page.locator(`.backlog-card[data-backlog-id="${backlogItemId}"]`).first();
    await expect(backlogCard).toBeVisible({ timeout: 10_000 });

    const targetSlot = page.locator(
      `.schedule-slot[data-tech-id="${slot!.technicianId}"][data-date="${slot!.targetDate}"][data-slot-start-minutes="${slot!.startMinutes}"]`
    ).first();
    await expect(targetSlot).toBeVisible({ timeout: 10_000 });

    await backlogCard.dragTo(targetSlot);
    await waitForTaskPanel(page);

    await expect(taskTitleInput(page)).toHaveValue("Drag backlog template");
    await expect(taskPrioritySelect(page)).toHaveValue("2");
    await expect(taskSkillSelect(page)).toHaveValue("Advanced");
    await expect(taskInstructionsInput(page)).toHaveValue("Drag backlog instructions");
    await expect(taskStartInput(page)).toHaveValue(`${slot!.targetDate}T${String(Math.floor(slot!.startMinutes / 60)).padStart(2, "0")}:${String(slot!.startMinutes % 60).padStart(2, "0")}`);
    await expect(page.locator("#taskDetailEnd")).toHaveValue(`${slot!.targetDate}T${String(Math.floor(slot!.endMinutes / 60)).padStart(2, "0")}:${String(slot!.endMinutes % 60).padStart(2, "0")}`);
    await expect(page.locator("#taskDetailObject")).not.toHaveValue("");
    await expect(page.locator("#taskDetailLocation")).toHaveValue("Konstitucijos pr. 16, Vilnius");

    const createResponsePromise = page.waitForResponse(
      (resp) => /\/api\/tasks(\?|$)/.test(resp.url()) && resp.request().method() === "POST" && resp.ok(),
      { timeout: 15_000 }
    );
    await saveTask(page);

    const createResponse = await createResponsePromise;
    const createBody = await createResponse.json();
    const createdTaskId = Number(createBody?.id || 0);
    expect(createdTaskId).toBeGreaterThan(0);
    taskIdsToCleanup.push(createdTaskId);

    const taskDetail = await getTaskDetailViaApi(page, createdTaskId);
    expect(taskDetail.sourceBacklogItemId).toBe(backlogItemId);
    expect(taskDetail.technicianId).toBe(slot!.technicianId);
    expect(taskDetail.targetDate).toBe(slot!.targetDate);
    expect(taskDetail.startMinutes).toBe(slot!.startMinutes);
    expect(taskDetail.endMinutes).toBe(slot!.endMinutes);
  });

  test("warns instead of opening an empty create form when dragged backlog item is stale", async ({ page }) => {
    const slot = await getVisibleScheduleSlot(page, { slotIndex: 0 }).catch(() => null);
    test.skip(!slot, "No visible schedule slot available for stale backlog drag-drop.");

    await page.evaluate(() => (window as Window & { schedule?: any }).schedule?.backlog?.openBacklogPanel?.());
    await page.locator("#backlogItemsList").waitFor({ state: "visible", timeout: 10_000 });

    const targetSlot = page.locator(
      `.schedule-slot[data-tech-id="${slot!.technicianId}"][data-date="${slot!.targetDate}"][data-slot-start-minutes="${slot!.startMinutes}"]`
    ).first();
    await expect(targetSlot).toBeVisible({ timeout: 10_000 });

    const hadOpenTaskPanelBeforeDrop = await page.locator("#taskDetailModal").evaluate((el) => el.classList.contains("show"));

    await page.evaluate(({ technicianId, targetDate, startMinutes }) => {
      const slotEl = document.querySelector(
        `.schedule-slot[data-tech-id="${technicianId}"][data-date="${targetDate}"][data-slot-start-minutes="${startMinutes}"]`
      );
      if (!(slotEl instanceof HTMLElement)) {
        throw new Error("Target slot not found for stale backlog drag-drop.");
      }

      const scheduleWindow = (window as Window & { schedule?: any });
      scheduleWindow.schedule = scheduleWindow.schedule || {};
      scheduleWindow.schedule.backlog = scheduleWindow.schedule.backlog || {};
      scheduleWindow.schedule.backlog._draggingItem = {
        id: "999999999",
        itemType: 1,
        _lastSlotMinutes: null,
        _lastRow: null,
      };

      const rect = slotEl.getBoundingClientRect();
      const dragOver = new DragEvent("dragover", {
        bubbles: true,
        cancelable: true,
        clientX: rect.left + rect.width / 2,
        clientY: rect.top + rect.height / 2,
      });
      slotEl.dispatchEvent(dragOver);

      const drop = new DragEvent("drop", {
        bubbles: true,
        cancelable: true,
        clientX: rect.left + rect.width / 2,
        clientY: rect.top + rect.height / 2,
      });
      slotEl.dispatchEvent(drop);
    }, slot!);

    const warningToast = page.locator(".schedule-toast--warning").filter({ hasText: /Nepavyko rasti tempiamo sablono/i }).first();
    await expect(warningToast).toBeVisible({ timeout: 10_000 });

    if (!hadOpenTaskPanelBeforeDrop) {
      await expect(page.locator("#taskDetailModal")).not.toHaveClass(/show/);
    }
  });
});
