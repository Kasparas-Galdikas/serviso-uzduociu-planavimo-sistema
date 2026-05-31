import { expect, test } from "@playwright/test";

import {
  authenticateAsManager,
  authenticateAsTechnician,
  switchToManager,
  switchToTechnician,
} from "../../actions/auth.actions";
import { openDaySchedule, openSchedule } from "../../actions/schedule.actions";
import { backlogPanel, taskPanel, taskSaveBtn } from "../../fixtures/selectors";
import {
  createManagerTaskAndOpenPanel,
  createManagerTaskViaApi,
} from "../../flows/task-access.flows";
import { openBacklogPlanningFlow } from "../../flows/plan.flows";
import { getCurrentUserContext } from "../../support/auth-api";
import { defaultPlanningDate } from "../../support/date";
import { openDraftDaySchedule } from "../../flows/task.flows";
import {
  cleanupTaskViaApi,
  createTaskViaApi,
  getVisibleScheduleSlotForTechnician,
} from "../../support/task-api";

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

  test("manager sees the backlog/templates planning entry on the schedule page", async ({ page }) => {
    await openSchedule(page);
    await expect(page.getByRole("button", { name: /Darb.*ablon/i }).first()).toBeVisible();

    await openBacklogPlanningFlow(page);
    await expect(backlogPanel(page)).toBeVisible();
  });

  test("manager can create a task via the API", async ({ page }) => {
    const result = await createManagerTaskViaApi(page, { title: "MgrTask" });
    taskIdToCleanup = result.taskId;

    expect(taskIdToCleanup).toBeTruthy();
  });

  test("manager sees save button when opening a task in the panel", async ({ page }) => {
    const result = await createManagerTaskAndOpenPanel(page, { title: "SaveBtnTask" }, { title: "SaveBtnTask" });
    taskIdToCleanup = result.taskId;

    await expect(taskSaveBtn(page)).toBeVisible();
  });
});

test.describe("technician permissions", () => {
  let taskIdToCleanup: number | null = null;

  test.beforeEach(async ({ page }) => {
    await authenticateAsTechnician(page);
  });

  test.afterEach(async ({ page }) => {
    if (taskIdToCleanup !== null) {
      await switchToManager(page);
      await cleanupTaskViaApi(page, taskIdToCleanup);
      taskIdToCleanup = null;
    }
  });

  test("technician is forbidden from creating a backlog draft plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/backlog-draft/ensure", {
      data: {
        startDate: "2099-01-05",
        endDate: "2099-01-18",
        weeks: 2,
      },
    });
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from applying a draft plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/backlog-draft/apply-to-active");
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from discarding a plan via the API (403)", async ({ page }) => {
    const response = await page.request.post("/api/plans/discard");
    expect([403, 404]).toContain(response.status());
  });

  test("technician is forbidden from creating tasks via the API (403)", async ({ page }) => {
    await openDaySchedule(page);
    const response = await page.request.post("/api/tasks", {
      data: {
        technicianId: 1,
        title: "ForbidTask",
        targetDate: "2099-01-01",
        startMinutes: 540,
        endMinutes: 570,
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

  test("technician does not see the backlog/templates planning entry on the schedule page", async ({ page }) => {
    await openSchedule(page);
    await expect(page.getByRole("button", { name: /Darb.*ablon/i })).toHaveCount(0);
    await expect(backlogPanel(page)).toBeHidden();
  });

  test("technician does not see an assigned draft task in draft scope", async ({ page }) => {
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await openDraftDaySchedule(page, defaultPlanningDate());
    const draftSlot = await getVisibleScheduleSlotForTechnician(page, currentUser.technicianId!, {
      minTargetDate: defaultPlanningDate(),
    });

    taskIdToCleanup = await createTaskViaApi(page, {
      technicianId: draftSlot.technicianId,
      targetDate: draftSlot.targetDate,
      startMinutes: draftSlot.startMinutes,
      endMinutes: draftSlot.endMinutes,
      title: "ReadOnlyTask",
    });

    await switchToTechnician(page);
    await page.goto(`/Schedule?view=day&date=${draftSlot.targetDate}&technicianId=${currentUser.technicianId}&planScope=draft`);
    await expect(page.locator(`.task-card[data-task-id="${taskIdToCleanup}"]`)).toHaveCount(0);
    await expect(taskPanel(page)).not.toHaveClass(/show/);
  });

  test("technician is in view-only mode (no edit mode toggle enabled)", async ({ page }) => {
    await openSchedule(page);
    const editToggle = page.locator("[data-edit-toggle]");
    const isVisible = await editToggle.isVisible().catch(() => false);
    expect(isVisible).toBe(false);
  });
});
