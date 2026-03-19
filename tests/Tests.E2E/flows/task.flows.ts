import type { Locator, Page } from "@playwright/test";
import {
  chooseCreateTaskFromSlot,
  fillTaskForm,
  openFirstScheduleSlot,
  saveTask,
  waitForTaskPanel,
  type TaskFormInput,
} from "../actions/task.actions";
import { createDraftViaApi } from "../support/plan-api";

export async function openDraftDaySchedule(page: Page): Promise<string> {
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=day&date=${draft.startDate}`);
  await page.waitForLoadState("networkidle");
  return draft.startDate;
}

export async function openCreateTaskPanelFromFirstSlot(page: Page): Promise<void> {
  await openFirstScheduleSlot(page);
  await chooseCreateTaskFromSlot(page);
  await waitForTaskPanel(page);
}

export async function createTaskInDraftThroughUi(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  await openDraftDaySchedule(page);
  await openCreateTaskPanelFromFirstSlot(page);
  await fillTaskForm(page, input);
  await saveTask(page);

  const card = page.locator(".task-card").filter({
    has: page.locator(".task-title", { hasText: input.title }),
  }).first();

  return { title: input.title, card };
}
