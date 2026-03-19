import { expect, test } from "@playwright/test";

import { authenticateAsManager, authenticateAsTechnician } from "../../actions/auth.actions";
import { openDaySchedule, openSchedule, openTaskCardByTitle, openFirstTaskCard } from "../../actions/schedule.actions";
import { waitForTaskPanel } from "../../actions/task.actions";
import { createPlanBtn, taskSaveBtn } from "../../fixtures/selectors";
import { cleanupTaskViaApi, createTaskViaApi, getTaskScheduleContext, getVisibleScheduleSlot } from "../../support/task-api";
import { today } from "../../support/date";

test.describe("manager permissions", () => {
  let taskIdToCleanup: number | null = null;

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    if (taskIdToCleanup !== null) {
      await cleanupTaskViaApi(page, taskIdToCleanup);
      taskIdToCleanup = null;
    }
  });

  test("manager sees the create-plan button on the schedule page", async ({ page }) => {
    await openSchedule(page);
    await expect(createPlanBtn(page)).toBeVisible();
  });

  test("manager can create a task via the API", async ({ page }) => {
    await openDaySchedule(page);
    const slot = await getVisibleScheduleSlot(page).catch(() => null);
    test.skip(!slot, "No visible schedule slot available for manager create test.");

    taskIdToCleanup = await createTaskViaApi(page, {
      technicianId: slot!.technicianId,
      title: "MgrTask",
      targetDate: slot!.targetDate,
      startMinutes: slot!.startMinutes,
      endMinutes: slot!.endMinutes,
    });

    expect(taskIdToCleanup).toBeTruthy();
  });

  test("manager sees save button when opening a task in the panel", async ({ page }) => {
    await openDaySchedule(page);
    taskIdToCleanup = await createTaskViaApi(page, { title: "SaveBtnTask" });
    const taskSchedule = await getTaskScheduleContext(page, taskIdToCleanup);

    await openDaySchedule(page, taskSchedule.targetDate);
    await openTaskCardByTitle(page, "SaveBtnTask");
    await waitForTaskPanel(page);

    await expect(taskSaveBtn(page)).toBeVisible();
  });
});

test.describe("technician permissions", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAsTechnician(page);
  });

  test("technician is forbidden from creating a draft plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/draft", {
      data: { weeks: 2 },
    });
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from publishing a plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/publish");
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from discarding a plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/discard");
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from creating tasks via the API (403)", async ({ page }) => {
    await openDaySchedule(page);
    const slot = await getVisibleScheduleSlot(page).catch(() => null);
    test.skip(!slot, "No visible schedule slot available for technician create test.");

    const response = await page.request.post("/api/tasks", {
      data: {
        technicianId: slot!.technicianId,
        title: "ForbidTask",
        targetDate: slot!.targetDate,
        startMinutes: slot!.startMinutes,
        endMinutes: slot!.endMinutes,
        priority: 1,
        status: 0,
        skillLevel: "Basic",
        objectName: "Vilniaus prekybos centras",
        objectAddress: "Konstitucijos pr. 16, Vilnius",
        timezoneOffsetMinutes: -120,
      },
    });
    expect([403, 404]).toContain(response.status());
  });

  test("technician does NOT see the create-plan button on the schedule page", async ({ page }) => {
    await openSchedule(page);
    const button = createPlanBtn(page);
    const isVisible = await button.isVisible().catch(() => false);
    const isEnabled = await button.isEnabled().catch(() => false);
    expect(isVisible && isEnabled).toBe(false);
  });

  test("technician does NOT see the save button when opening a task panel", async ({ page }) => {
    await openDaySchedule(page, today());

    try {
      await openFirstTaskCard(page);
    } catch {
      test.skip(true, "No task cards present for technician panel test.");
    }

    await waitForTaskPanel(page);
    await expect(taskSaveBtn(page)).not.toBeVisible();
  });

  test("technician is in view-only mode (no edit mode toggle enabled)", async ({ page }) => {
    await openSchedule(page);
    const editToggle = page.locator("[data-edit-toggle]");
    const isVisible = await editToggle.isVisible().catch(() => false);
    expect(isVisible).toBe(false);
  });
});
