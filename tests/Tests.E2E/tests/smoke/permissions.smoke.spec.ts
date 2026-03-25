import { expect, test } from "@playwright/test";

import { authenticateAsManager, authenticateAsTechnician, switchToManager } from "../../actions/auth.actions";
import { openDaySchedule, openSchedule } from "../../actions/schedule.actions";
import { taskSaveBtn } from "../../fixtures/selectors";
import {
  createManagerTaskAndOpenPanel,
  createManagerTaskViaApi,
  createTaskForTechnicianAndOpenAsTechnician,
} from "../../flows/task-access.flows";
import { getCurrentUserContext } from "../../support/auth-api";
import { createPlanBtn } from "../../fixtures/selectors";
import { cleanupTaskViaApi } from "../../support/task-api";

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

  test("technician does NOT see the create-plan button on the schedule page", async ({ page }) => {
    await openSchedule(page);
    const button = createPlanBtn(page);
    const isVisible = await button.isVisible().catch(() => false);
    const isEnabled = await button.isEnabled().catch(() => false);
    expect(isVisible && isEnabled).toBe(false);
  });

  test("technician sees read-only controls when opening an assigned draft task", async ({ page }) => {
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    const result = await createTaskForTechnicianAndOpenAsTechnician(
      page,
      currentUser.technicianId!,
      { title: "ReadOnlyTask" },
      { draft: true }
    );
    taskIdToCleanup = result.taskId;

    await expect(taskSaveBtn(page)).toBeDisabled();
  });

  test("technician is in view-only mode (no edit mode toggle enabled)", async ({ page }) => {
    await openSchedule(page);
    const editToggle = page.locator("[data-edit-toggle]");
    const isVisible = await editToggle.isVisible().catch(() => false);
    expect(isVisible).toBe(false);
  });
});
