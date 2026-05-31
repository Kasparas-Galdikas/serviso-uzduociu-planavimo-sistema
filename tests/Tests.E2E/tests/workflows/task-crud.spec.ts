import { expect, test, type Page } from "@playwright/test";
import { authenticateAsManager } from "../../actions/auth.actions";
import {
  confirmTaskDeletion,
  editPanelInstructions,
  editPanelPriority,
  editPanelSkill,
  openTaskDeleteConfirmation,
  reopenTaskCard,
  saveTask,
  saveTaskAndWaitForVersionChange,
} from "../../actions/task.actions";
import { successToasts, taskPanel, taskPrioritySelect, taskSkillSelect, taskTitleInput } from "../../fixtures/selectors";
import {
  createActiveTaskAndOpenPanel,
  createActiveTaskAndReturnCard,
  openActiveDayScheduleAndTaskCreatePanel,
} from "../../flows/task.flows";
import { discardDraftIfExists } from "../../support/plan-api";
import { cleanupTaskViaApi } from "../../support/task-api";

async function reloadTaskCard(page: Page, taskId: number) {
  await page.reload({ waitUntil: "networkidle" });
  const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
  await expect(card).toBeVisible({ timeout: 10_000 });
  return card;
}

test.describe("task CRUD workflows", () => {
  test.describe.configure({ mode: "serial" });

  let taskIdToCleanup: number | null = null;

  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
  });

  test.afterEach(async ({ page }) => {
    if (taskIdToCleanup !== null) {
      await cleanupTaskViaApi(page, taskIdToCleanup);
      taskIdToCleanup = null;
    }

    await discardDraftIfExists(page);
  });

  test("creates a task by clicking an empty slot, filling the panel, and saving", async ({ page }) => {
    const title = `E2E${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndReturnCard(page, { title });

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(card).toBeVisible({ timeout: 10_000 });

    const taskId = await card.getAttribute("data-task-id");
    taskIdToCleanup = taskId ? Number(taskId) : null;
  });

  test("updates a task title via the task panel and persists the change", async ({ page }) => {
    const initialTitle = `E2E${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndOpenPanel(page, { title: initialTitle });

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(card).toBeVisible({ timeout: 10_000 });

    const taskId = Number(await card.getAttribute("data-task-id"));
    taskIdToCleanup = taskId;
    const newTitle = `U${Date.now().toString().slice(-6)}`;

    await taskTitleInput(page).fill(newTitle);
    await saveTaskAndWaitForVersionChange(page);
    const refreshedCard = await reloadTaskCard(page, taskId);
    await expect(refreshedCard.locator(".task-title")).toHaveText(newTitle, { timeout: 10_000 });
  });

  test("deletes a task via the card delete button and removes it from the grid", async ({ page }) => {
    const title = `D${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndReturnCard(page, { title });

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(card).toBeVisible({ timeout: 10_000 });

    const taskId = await card.getAttribute("data-task-id");
    taskIdToCleanup = taskId ? Number(taskId) : null;

    await openTaskDeleteConfirmation(card, page);
    await confirmTaskDeletion(page);

    await expect(successToasts(page).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(`.task-card[data-task-id="${taskId}"]`)).not.toBeVisible({ timeout: 10_000 });
    taskIdToCleanup = null;
  });

  test("shows an error message when saving a task with no title", async ({ page }) => {
    await openActiveDayScheduleAndTaskCreatePanel(page);

    await taskTitleInput(page).fill("");
    await saveTask(page);

    await expect(page.getByText("Pavadinimas yra privalomas.")).toBeVisible({ timeout: 5_000 });
    await expect(taskPanel(page)).toBeVisible();
  });

  test("updates task priority via the panel and persists the change", async ({ page }) => {
    const title = `P${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndOpenPanel(page, { title, priority: "1" });

    await expect(card).toBeVisible({ timeout: 10_000 });
    const taskId = Number(await card.getAttribute("data-task-id"));
    taskIdToCleanup = taskId;

    await editPanelPriority(page, "2");
    await saveTaskAndWaitForVersionChange(page);

    const refreshedCard = await reloadTaskCard(page, taskId);
    await reopenTaskCard(page, refreshedCard);
    await expect(taskPrioritySelect(page)).toHaveValue("2");
  });

  test("updates task skill level via the panel and persists the change", async ({ page }) => {
    const title = `S${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndOpenPanel(page, { title, skill: "Basic" });

    await expect(card).toBeVisible({ timeout: 10_000 });
    const taskId = Number(await card.getAttribute("data-task-id"));
    taskIdToCleanup = taskId;

    await editPanelSkill(page, "Advanced");
    await saveTaskAndWaitForVersionChange(page);

    const refreshedCard = await reloadTaskCard(page, taskId);
    await reopenTaskCard(page, refreshedCard);
    await expect(taskSkillSelect(page)).toHaveValue("Advanced");
  });

  test("updates task instructions via the panel and persists the change", async ({ page }) => {
    const title = `I${Date.now().toString().slice(-6)}`;
    const { card } = await createActiveTaskAndOpenPanel(page, { title });

    await expect(card).toBeVisible({ timeout: 10_000 });
    const taskId = Number(await card.getAttribute("data-task-id"));
    taskIdToCleanup = taskId;

    const instructions = `Test instructions ${Date.now()}`;
    await editPanelInstructions(page, instructions);
    await saveTaskAndWaitForVersionChange(page);

    const refreshedCard = await reloadTaskCard(page, taskId);
    await reopenTaskCard(page, refreshedCard);
    await expect(page.locator("#taskDetailInstructions")).toHaveValue(instructions);
  });

  test.skip("overlapping draft task creation via API is auto-replanned instead of rejected", async () => {});
});
