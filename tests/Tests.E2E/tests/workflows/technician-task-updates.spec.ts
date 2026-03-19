import { expect, test } from "@playwright/test";
import { switchToManager, switchToTechnician } from "../../actions/auth.actions";
import { openDaySchedule, openTaskCardById, openTechnicianDaySchedule } from "../../actions/schedule.actions";
import {
  editTechnicianComment,
  editTechnicianStatus,
  saveTask,
  waitForTaskPanel,
} from "../../actions/task.actions";
import {
  successToasts,
  taskSaveBtn,
  taskStatusSelect,
  taskTechnicianCommentInput,
} from "../../fixtures/selectors";
import { openDraftDaySchedule } from "../../flows/task.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { getCurrentUserContext } from "../../support/auth-api";
import {
  cleanupTaskViaApi,
  createTaskViaApi,
  getAnotherTechnicianId,
  getTaskDetailViaApi,
  getVisibleScheduleSlotForTechnician,
  updateTaskAsTechnicianViaApi,
} from "../../support/task-api";

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

  test("technician marks own active task as completed and the comment persists", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await discardDraftIfExists(page);
    await openDaySchedule(page);

    const slot = await getVisibleScheduleSlotForTechnician(page, currentUser.technicianId!).catch(() => null);
    test.skip(!slot, "No visible active-plan slot available for the seeded technician.");

    const taskId = await createTaskViaApi(page, { title: "TechDone", technicianId: currentUser.technicianId!, ...slot! });
    taskIdsToCleanup.push(taskId);

    await switchToTechnician(page);
    await openTechnicianDaySchedule(page, currentUser.technicianId!, slot!.targetDate);
    await openTaskCardById(page, taskId);
    await waitForTaskPanel(page);

    const comment = `Done ${Date.now()}`;
    await editTechnicianStatus(page, "1");
    await editTechnicianComment(page, comment);
    await saveTask(page);

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, taskId);
    expect(updated.status).toBe(1);
    expect(updated.technicianComment).toBe(comment);
    expect(updated.isEditable).toBe(false);
  });

  test("technician marks own active task as problem and the comment persists", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await discardDraftIfExists(page);
    await openDaySchedule(page);

    const slot = await getVisibleScheduleSlotForTechnician(page, currentUser.technicianId!, { slotIndex: 1 }).catch(() => null);
    test.skip(!slot, "No second visible active-plan slot available for the seeded technician.");

    const taskId = await createTaskViaApi(page, { title: "TechProb", technicianId: currentUser.technicianId!, ...slot! });
    taskIdsToCleanup.push(taskId);

    await switchToTechnician(page);
    await openTechnicianDaySchedule(page, currentUser.technicianId!, slot!.targetDate);
    await openTaskCardById(page, taskId);
    await waitForTaskPanel(page);

    const comment = `Prob ${Date.now()}`;
    await editTechnicianStatus(page, "2");
    await editTechnicianComment(page, comment);
    await saveTask(page);

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, taskId);
    expect(updated.status).toBe(2);
    expect(updated.technicianComment).toBe(comment);
    expect(updated.isEditable).toBe(false);
  });

  test("technician cannot update another technician's active task via the technician endpoint", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await discardDraftIfExists(page);
    await openDaySchedule(page);

    const otherTechnicianId = await getAnotherTechnicianId(page, currentUser.technicianId!);
    const slot = await getVisibleScheduleSlotForTechnician(page, otherTechnicianId).catch(() => null);
    test.skip(!slot, "No visible active-plan slot available for a second technician.");

    const taskId = await createTaskViaApi(page, { title: "OtherTech", technicianId: otherTechnicianId, ...slot! });
    taskIdsToCleanup.push(taskId);
    const detail = await getTaskDetailViaApi(page, taskId);

    await switchToTechnician(page);
    const response = await updateTaskAsTechnicianViaApi(page, taskId, {
      status: 1,
      comment: "Forbidden update",
      version: detail.version,
    });

    expect(response.status).toBe(403);
    expect(String((response.body as { message?: string } | null)?.message ?? response.body ?? "")).toMatch(/savo|redaguoti|teisiu/i);
  });

  test("technician sees an assigned draft task as read-only", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    await switchToManager(page);
    await discardDraftIfExists(page);
    const draftDate = await openDraftDaySchedule(page);

    const slot = await getVisibleScheduleSlotForTechnician(page, currentUser.technicianId!).catch(() => null);
    test.skip(!slot, "No visible draft slot available for the seeded technician.");

    const taskId = await createTaskViaApi(page, { title: "DraftTech", technicianId: currentUser.technicianId!, ...slot! });
    taskIdsToCleanup.push(taskId);

    await switchToTechnician(page);
    await openTechnicianDaySchedule(page, currentUser.technicianId!, draftDate);
    await openTaskCardById(page, taskId);
    await waitForTaskPanel(page);

    await expect(taskSaveBtn(page)).toBeDisabled();
    await expect(taskStatusSelect(page)).toBeDisabled();
    await expect(taskTechnicianCommentInput(page)).toBeDisabled();
  });
});
