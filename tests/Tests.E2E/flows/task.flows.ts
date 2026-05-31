import type { Locator, Page } from "@playwright/test";
import { discardDraftIfExists } from "../support/plan-api";
import { openDaySchedule } from "../actions/schedule.actions";
import { defaultPlanningDate } from "../support/date";
import {
  chooseCreateTaskFromSlot,
  fillTaskForm,
  openFirstScheduleSlot,
  saveTask,
  saveTaskAndWaitForCreateResult,
  waitForTaskPanel,
  type TaskFormInput,
} from "../actions/task.actions";
import { createDraftViaApi } from "../support/plan-api";
import { createTaskViaApi } from "../support/task-api";

export async function openDraftDaySchedule(page: Page, targetDate?: string): Promise<string> {
  await discardDraftIfExists(page);
  const draft = await createDraftViaApi(page);
  const date = targetDate ?? draft.startDate;
  await page.goto(`/Schedule?view=day&date=${date}&planScope=draft`);
  await page.waitForLoadState("networkidle");
  return date;
}

export async function openActiveDaySchedule(page: Page): Promise<string> {
  await discardDraftIfExists(page);
  await openDaySchedule(page, defaultPlanningDate());
  await page.waitForLoadState("networkidle");
  const date = new URL(page.url()).searchParams.get("date");
  return date ?? "";
}

export async function openCreateTaskPanelFromFirstSlot(page: Page): Promise<void> {
  const slotContext = await openFirstScheduleSlot(page);
  await chooseCreateTaskFromSlot(page, slotContext);
  await waitForTaskPanel(page);
}

export async function openDraftDayScheduleAndTaskCreatePanel(page: Page): Promise<string> {
  const date = await openDraftDaySchedule(page);
  await openCreateTaskPanelFromFirstSlot(page);
  return date;
}

export async function openActiveDayScheduleAndTaskCreatePanel(page: Page): Promise<string> {
  const date = await openActiveDaySchedule(page);
  await openCreateTaskPanelFromFirstSlot(page);
  return date;
}

export async function createDraftTaskAndReturnCard(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  await openDraftDayScheduleAndTaskCreatePanel(page);
  await fillTaskForm(page, input);
  await saveTask(page);

  const card = page.locator(".task-card").filter({
    has: page.locator(".task-title", { hasText: input.title }),
  }).first();

  await card.waitFor({ state: "visible", timeout: 10_000 });
  return { title: input.title, card };
}

export async function createTaskInDraftThroughUi(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  return await createDraftTaskAndReturnCard(page, input);
}

export async function createActiveTaskAndReturnCard(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  await openActiveDayScheduleAndTaskCreatePanel(page);
  await fillTaskForm(page, input);
  const createResult = await saveTaskAndWaitForCreateResult(page);
  if (!createResult.ok || createResult.id == null) {
    throw new Error(`Task create failed with status ${createResult.status}: ${JSON.stringify(createResult.body)}`);
  }

  const card = page.locator(`.task-card[data-task-id="${createResult.id}"]`);

  await card.waitFor({ state: "visible", timeout: 10_000 });
  return { title: input.title, card };
}

export async function createActiveTaskAndOpenPanel(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  const result = await createActiveTaskAndReturnCard(page, input);
  await result.card.click();
  await waitForTaskPanel(page);
  return result;
}

export async function createDraftTaskAndOpenPanel(
  page: Page,
  input: TaskFormInput
): Promise<{ title: string; card: Locator }> {
  const result = await createDraftTaskAndReturnCard(page, input);
  await result.card.click();
  await waitForTaskPanel(page);
  return result;
}

export async function createDraftTaskForInteraction(
  page: Page,
  overrides: Record<string, unknown> = {},
  options?: { preferredCity?: string; slotIndex?: number; date?: string }
): Promise<{ date: string; taskId: number; card: Locator }> {
  const date = options?.date ?? await openDraftDaySchedule(page);
  const taskId = await createTaskViaApi(page, overrides, options);

  await page.goto(`/Schedule?view=day&date=${date}`);
  await page.waitForLoadState("networkidle");

  const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
  await card.waitFor({ state: "visible", timeout: 10_000 });
  return { date, taskId, card };
}
