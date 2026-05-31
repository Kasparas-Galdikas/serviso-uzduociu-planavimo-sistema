import { expect, test } from "@playwright/test";
import { switchToManager, switchToTechnician } from "../../actions/auth.actions";
import {
  editTechnicianComment,
  editTechnicianStatus,
  saveTask,
} from "../../actions/task.actions";
import {
  taskPanel,
  warningToasts,
} from "../../fixtures/selectors";
import {
  createTaskForOtherTechnician,
  createTaskForTechnicianAndOpenAsTechnician,
} from "../../flows/task-access.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { getCurrentUserContext } from "../../support/auth-api";
import {
  cleanupTaskViaApi,
  createTaskViaApi,
  getVisibleScheduleSlotForTechnician,
  getTaskDetailViaApi,
  updateTaskAsTechnicianViaApi,
} from "../../support/task-api";
import { openDraftDaySchedule } from "../../flows/task.flows";
import { defaultPlanningDate } from "../../support/date";

test.describe("technician task workflows", () => {
  test.describe.configure({ mode: "serial" });

  const taskIdsToCleanup: number[] = [];

  test.afterEach(async ({ page }) => {
    await switchToManager(page);

    for (const taskId of taskIdsToCleanup) {
      await cleanupTaskViaApi(page, taskId);
    }

    taskIdsToCleanup.length = 0;
    await discardDraftIfExists(page);
  });

  test("technician cannot mark own active task as completed before the task starts", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    const result = await createTaskForTechnicianAndOpenAsTechnician(
      page,
      currentUser.technicianId!,
      { title: "TechDone" }
    );
    taskIdsToCleanup.push(result.taskId);

    const comment = `Done ${Date.now()}`;
    await editTechnicianStatus(page, "1");
    await editTechnicianComment(page, comment);
    await saveTask(page);

    await expect(warningToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/Negalima keisti statuso prieš prasidedant užduočiai/i)).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, result.taskId);
    expect(updated.status).not.toBe(1);
    expect(updated.technicianComment ?? "").not.toBe(comment);
    expect(updated.isEditable).toBeTruthy();
  });

  test("technician cannot mark own active task as problem before the task starts", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    const result = await createTaskForTechnicianAndOpenAsTechnician(
      page,
      currentUser.technicianId!,
      { title: "TechProb" },
      { slotIndex: 1 }
    );
    taskIdsToCleanup.push(result.taskId);

    const comment = `Prob ${Date.now()}`;
    await editTechnicianStatus(page, "2");
    await editTechnicianComment(page, comment);
    await saveTask(page);

    await expect(warningToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/Negalima keisti statuso prieš prasidedant užduočiai/i)).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, result.taskId);
    expect(updated.status).not.toBe(2);
    expect(updated.technicianComment ?? "").not.toBe(comment);
    expect(updated.isEditable).toBeTruthy();
  });

  test("technician cannot update another technician's active task via the technician endpoint", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    const result = await createTaskForOtherTechnician(page, currentUser.technicianId!, { title: "OtherTech" });
    taskIdsToCleanup.push(result.taskId);
    const detail = await getTaskDetailViaApi(page, result.taskId);

    await switchToTechnician(page);
    const response = await updateTaskAsTechnicianViaApi(page, result.taskId, {
      status: 1,
      comment: "Forbidden update",
      version: detail.version,
    });

    expect(response.status).toBe(403);
    expect(String((response.body as { message?: string } | null)?.message ?? response.body ?? "")).toMatch(/savo|redaguoti|teisiu/i);
  });

  test("technician does not see an assigned draft task in draft scope", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await openDraftDaySchedule(page, defaultPlanningDate());
    const draftSlot = await getVisibleScheduleSlotForTechnician(page, currentUser.technicianId!, {
      minTargetDate: defaultPlanningDate(),
    });
    const taskId = await createTaskViaApi(page, {
      technicianId: draftSlot.technicianId,
      targetDate: draftSlot.targetDate,
      startMinutes: draftSlot.startMinutes,
      endMinutes: draftSlot.endMinutes,
      title: "DraftTech",
    });
    taskIdsToCleanup.push(taskId);

    await switchToTechnician(page);
    await page.goto(`/Schedule?view=day&date=${draftSlot.targetDate}&technicianId=${currentUser.technicianId}&planScope=draft`);
    await expect(page.locator(`.task-card[data-task-id="${taskId}"]`)).toHaveCount(0);
    await expect(taskPanel(page)).not.toHaveClass(/show/);
  });
});
