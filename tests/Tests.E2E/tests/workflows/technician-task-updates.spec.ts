import { expect, test } from "@playwright/test";
import { switchToManager, switchToTechnician } from "../../actions/auth.actions";
import {
  editTechnicianComment,
  editTechnicianStatus,
  saveTask,
} from "../../actions/task.actions";
import {
  successToasts,
  taskSaveBtn,
  taskStatusSelect,
  taskTechnicianCommentInput,
} from "../../fixtures/selectors";
import {
  createTaskForOtherTechnician,
  createTaskForTechnicianAndOpenAsTechnician,
} from "../../flows/task-access.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { getCurrentUserContext } from "../../support/auth-api";
import {
  cleanupTaskViaApi,
  getTaskDetailViaApi,
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

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, result.taskId);
    expect(updated.status).toBe(1);
    expect(updated.technicianComment).toBe(comment);
    expect(updated.isEditable).toBe(false);
  });

  test("technician marks own active task as problem and the comment persists", async ({ page }) => {
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

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });

    const updated = await getTaskDetailViaApi(page, result.taskId);
    expect(updated.status).toBe(2);
    expect(updated.technicianComment).toBe(comment);
    expect(updated.isEditable).toBe(false);
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

  test("technician sees an assigned draft task as read-only", async ({ page }) => {
    await switchToTechnician(page);
    const currentUser = await getCurrentUserContext(page);
    test.skip(!currentUser.technicianId, "Current seeded technician user has no technician id.");

    const result = await createTaskForTechnicianAndOpenAsTechnician(
      page,
      currentUser.technicianId!,
      { title: "DraftTech" },
      { draft: true }
    );
    taskIdsToCleanup.push(result.taskId);

    await expect(taskSaveBtn(page)).toBeDisabled();
    await expect(taskStatusSelect(page)).toBeDisabled();
    await expect(taskTechnicianCommentInput(page)).toBeDisabled();
  });
});