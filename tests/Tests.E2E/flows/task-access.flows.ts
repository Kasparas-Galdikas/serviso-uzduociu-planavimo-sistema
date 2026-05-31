import type { Locator, Page } from "@playwright/test";
import { discardDraftIfExists } from "../support/plan-api";
import {
  createTaskViaApi,
  getAnotherTechnicianId,
  getTaskScheduleContext,
  getVisibleScheduleSlotForTechnician,
  normalizeImplicitScheduleToSafeFuture,
  type ScheduleSlotContext,
} from "../support/task-api";
import { compareLocalDates, defaultPlanningDate, today } from "../support/date";
import { switchToManager, switchToTechnician } from "../actions/auth.actions";
import {
  openDaySchedule,
  openTaskCardById,
  openTaskCardByTitle,
  openTechnicianDaySchedule,
  openWeekSchedule,
} from "../actions/schedule.actions";
import { waitForTaskPanel } from "../actions/task.actions";
import { openDraftDaySchedule } from "./task.flows";

type SlotOptions = {
  slotIndex?: number;
};

function nextPlanningDate(): string {
  return defaultPlanningDate();
}

export async function openManagerPlanningDay(page: Page, preferredDate = nextPlanningDate()): Promise<string> {
  await discardDraftIfExists(page);
  await openDaySchedule(page, preferredDate);
  return preferredDate;
}

async function openTechnicianDayWithAvailability(page: Page, technicianId: number, preferredDate = nextPlanningDate()): Promise<string> {
  await openWeekSchedule(page, preferredDate);

  const candidateDates = await page
    .locator(`a[href*="view=day"][href*="technicianId=${technicianId}"]`)
    .evaluateAll((links) =>
      links
        .map((link) => {
          const href = link.getAttribute("href") ?? "";
          const match = href.match(/[?&]date=(\d{4}-\d{2}-\d{2})/);
          return match?.[1] ?? null;
        })
        .filter((value): value is string => Boolean(value))
    );

  const sortedDates = [...new Set(candidateDates)].sort((left, right) => compareLocalDates(left, right));
  const selectedDate = sortedDates.find((date) => compareLocalDates(date, preferredDate) >= 0) ?? sortedDates[0];
  if (!selectedDate) {
    throw new Error(`No visible schedule day found for technician ${technicianId}`);
  }

  await openTechnicianDaySchedule(page, technicianId, selectedDate);
  return selectedDate;
}

async function createTaskForTechnicianWithSlotRetry(
  page: Page,
  technicianId: number,
  overrides: Record<string, unknown>,
  options?: SlotOptions
): Promise<{ taskId: number; schedule: ScheduleSlotContext }> {
  const startIndex = options?.slotIndex ?? 0;
  const maxAttempts = 8;
  let lastError: string | null = null;

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    const schedule = await getVisibleScheduleSlotForTechnician(page, technicianId, {
      slotIndex: startIndex + attempt,
      minTargetDate: nextPlanningDate(),
    });
    const safeSchedule = normalizeImplicitScheduleToSafeFuture(schedule, { minTargetDate: nextPlanningDate() });

    try {
      const taskId = await createTaskViaApi(page, { ...safeSchedule, ...overrides });
      return { taskId, schedule: safeSchedule };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      lastError = message;

      if (!/Failed to create task via API: (409|422|500)\b/.test(message) && !/Negalima planuoti praeityje/i.test(message)) {
        throw error;
      }
    }
  }

  throw new Error(`Failed to create technician task after slot retries: ${lastError ?? "unknown error"}`);
}

export async function openExistingTaskPanelForRole(
  page: Page,
  input: { role: "manager"; taskId: number; date?: string } | { role: "technician"; taskId: number; technicianId: number; date?: string }
): Promise<Locator> {
  if (input.role === "manager") {
    await openDaySchedule(page, input.date ?? today());
  } else {
    await openTechnicianDaySchedule(page, input.technicianId, input.date ?? today());
  }

  const card = await openTaskCardById(page, input.taskId);
  await waitForTaskPanel(page);
  return card;
}

export async function createManagerTaskViaApi(
  page: Page,
  overrides: Record<string, unknown> = {},
  options?: SlotOptions
): Promise<{ taskId: number; schedule: ScheduleSlotContext }> {
  await openManagerPlanningDay(page);
  const taskId = await createTaskViaApi(page, overrides, options);
  const schedule = await getTaskScheduleContext(page, taskId);
  return { taskId, schedule };
}

export async function createManagerTaskAndOpenPanel(
  page: Page,
  overrides: Record<string, unknown> = {},
  options?: SlotOptions & { title?: string }
): Promise<{ taskId: number; card: Locator; schedule: ScheduleSlotContext }> {
  const { taskId, schedule } = await createManagerTaskViaApi(page, overrides, options);

  await openDaySchedule(page, schedule.targetDate);
  const card = options?.title
    ? await openTaskCardByTitle(page, options.title)
    : await openTaskCardById(page, taskId);
  await waitForTaskPanel(page);

  return { taskId, card, schedule };
}

export async function createTaskForTechnicianAndOpenAsTechnician(
  page: Page,
  technicianId: number,
  overrides: Record<string, unknown> = {},
  options?: SlotOptions & { draft?: boolean }
): Promise<{ taskId: number; targetDate: string; card: Locator }> {
  await switchToManager(page);

  let targetDate = today();
  if (options?.draft) {
    targetDate = await openDraftDaySchedule(page);
  } else {
    await discardDraftIfExists(page);
    targetDate = await openTechnicianDayWithAvailability(page, technicianId);
  }

  const { taskId, schedule } = await createTaskForTechnicianWithSlotRetry(page, technicianId, overrides, options);

  await switchToTechnician(page);
  let card: Locator;
  if (options?.draft) {
    await page.goto(`/Schedule?view=day&date=${targetDate}&technicianId=${technicianId}&planScope=draft`);
    card = await openTaskCardById(page, taskId);
    await waitForTaskPanel(page);
  } else {
    card = await openExistingTaskPanelForRole(page, {
      role: "technician",
      taskId,
      technicianId,
      date: schedule.targetDate,
    });
  }

  return {
    taskId,
    targetDate: options?.draft ? targetDate : schedule.targetDate,
    card,
  };
}

export async function createTaskForOtherTechnician(
  page: Page,
  excludedTechnicianId: number,
  overrides: Record<string, unknown> = {},
  options?: SlotOptions
): Promise<{ taskId: number; technicianId: number; schedule: ScheduleSlotContext }> {
  await switchToManager(page);
  await discardDraftIfExists(page);

  const technicianId = await getAnotherTechnicianId(page, excludedTechnicianId);
  await openTechnicianDayWithAvailability(page, technicianId);
  const { taskId, schedule } = await createTaskForTechnicianWithSlotRetry(page, technicianId, overrides, options);

  return {
    taskId,
    technicianId,
    schedule,
  };
}
