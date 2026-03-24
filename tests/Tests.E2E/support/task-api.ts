import type { Page } from "@playwright/test";
import { scheduleSlots } from "../fixtures/selectors";
import { compareLocalDates, defaultPlanningDate, isDateTimeInPast, today } from "./date";

export type ScheduleSlotContext = {
  technicianId: number;
  targetDate: string;
  startMinutes: number;
  endMinutes: number;
};

export type TaskScheduleContext = ScheduleSlotContext & {
  taskId: number;
};

export type TaskDetailContext = {
  taskId: number;
  version: number;
  technicianId: number;
  targetDate: string;
  startMinutes: number;
  endMinutes: number;
  status: number;
  technicianComment: string;
  planStatus: string;
  isEditable: boolean;
  isDeletable: boolean;
};

type ScheduleSelectionOptions = {
  preferredCity?: string;
  slotIndex?: number;
  minTargetDate?: string;
};

function parseMinutesFromDateTime(value: string): number {
  const time = String(value).split("T")[1]?.slice(0, 5);
  if (!time) {
    throw new Error(`Could not parse time from '${value}'`);
  }

  const [hours, minutes] = time.split(":").map(Number);
  return hours * 60 + minutes;
}

function getCurrentLocalSlotBoundary(): { date: string; minutes: number } {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");

  return {
    date: `${year}-${month}-${day}`,
    minutes: now.getHours() * 60 + now.getMinutes(),
  };
}

function compareSlots(left: { targetDate: string; startMinutes: number }, right: { targetDate: string; startMinutes: number }): number {
  if (left.targetDate !== right.targetDate) {
    return left.targetDate.localeCompare(right.targetDate);
  }

  return left.startMinutes - right.startMinutes;
}

function preferFutureSlots<T extends { targetDate: string; startMinutes: number }>(slots: T[], minTargetDate?: string): T[] {
  const boundary = getCurrentLocalSlotBoundary();
  const futureSlots = slots.filter(
    (slot) => slot.targetDate > boundary.date || (slot.targetDate === boundary.date && slot.startMinutes >= boundary.minutes + 60)
  );

  const minDateSlots = minTargetDate ? slots.filter((slot) => compareLocalDates(slot.targetDate, minTargetDate) >= 0) : [];
  const prioritized = minDateSlots.length ? minDateSlots : futureSlots.length ? futureSlots : slots;
  return [...prioritized].sort(compareSlots);
}

function isPastPlanningValidation(responseText: string): boolean {
  return /Negalima planuoti praeityje/i.test(responseText);
}

export function normalizeImplicitScheduleToSafeFuture<T extends ScheduleSlotContext>(
  schedule: T,
  options?: { minTargetDate?: string }
): T {
  const minTargetDate = options?.minTargetDate ?? defaultPlanningDate();
  let targetDate = schedule.targetDate;

  if (compareLocalDates(targetDate, minTargetDate) < 0) {
    targetDate = minTargetDate;
  }

  if (targetDate === today() && isDateTimeInPast(targetDate, schedule.startMinutes)) {
    targetDate = defaultPlanningDate();
  }

  return {
    ...schedule,
    targetDate,
  };
}

export async function getVisibleScheduleSlot(
  page: Page,
  options?: ScheduleSelectionOptions
): Promise<ScheduleSlotContext> {
  const preferredCity = options?.preferredCity;
  const slotIndex = options?.slotIndex ?? 0;

  const techniciansResponse = await page.request.get("/api/technicians");
  const technicians: Array<{ id: number; city?: string }> = techniciansResponse.ok() ? await techniciansResponse.json() : [];
  const cityByTechnicianId = new Map(technicians.map((technician) => [technician.id, technician.city ?? ""]));

  const slots = await scheduleSlots(page).evaluateAll((elements) =>
    elements.map((element) => ({
      technicianId: Number(element.getAttribute("data-tech-id")),
      targetDate: element.getAttribute("data-date") ?? "",
      startMinutes: Number(element.getAttribute("data-slot-start-minutes")),
    }))
  );

  const validSlots = slots
    .filter((slot) => Number.isFinite(slot.technicianId) && slot.targetDate && Number.isFinite(slot.startMinutes))
    .filter((slot) => slot.startMinutes >= 8 * 60 && slot.startMinutes + 30 <= 20 * 60);

  const sortedSlots = preferFutureSlots(validSlots, options?.minTargetDate);

  if (!sortedSlots.length) {
    throw new Error("No empty schedule slots available in the rendered schedule");
  }

  const selectedSlot = preferredCity
    ? sortedSlots.find((slot) => cityByTechnicianId.get(slot.technicianId)?.toLowerCase() === preferredCity.toLowerCase()) ??
      sortedSlots[Math.min(slotIndex, sortedSlots.length - 1)]
    : sortedSlots[Math.min(slotIndex, sortedSlots.length - 1)];

  return {
    technicianId: selectedSlot.technicianId,
    targetDate: selectedSlot.targetDate,
    startMinutes: selectedSlot.startMinutes,
    endMinutes: selectedSlot.startMinutes + 30,
  };
}

export async function getVisibleScheduleSlotForTechnician(
  page: Page,
  technicianId: number,
  options?: { slotIndex?: number; minTargetDate?: string }
): Promise<ScheduleSlotContext> {
  const slotIndex = options?.slotIndex ?? 0;

  const slots = await scheduleSlots(page).evaluateAll((elements, targetTechnicianId) =>
    elements
      .map((element) => ({
        technicianId: Number(element.getAttribute("data-tech-id")),
        targetDate: element.getAttribute("data-date") ?? "",
        startMinutes: Number(element.getAttribute("data-slot-start-minutes")),
      }))
      .filter((slot) => slot.technicianId === Number(targetTechnicianId)),
    technicianId
  );

  const validSlots = slots
    .filter((slot) => slot.targetDate && Number.isFinite(slot.startMinutes))
    .filter((slot) => slot.startMinutes >= 8 * 60 && slot.startMinutes + 30 <= 20 * 60);

  const sortedSlots = preferFutureSlots(validSlots, options?.minTargetDate);

  if (!sortedSlots.length) {
    throw new Error(`No visible empty schedule slots available for technician ${technicianId}`);
  }

  const selectedSlot = sortedSlots[Math.min(slotIndex, sortedSlots.length - 1)];
  return {
    technicianId: selectedSlot.technicianId,
    targetDate: selectedSlot.targetDate,
    startMinutes: selectedSlot.startMinutes,
    endMinutes: selectedSlot.startMinutes + 30,
  };
}

export async function getAnotherTechnicianId(page: Page, excludedTechnicianId: number): Promise<number> {
  const techniciansResponse = await page.request.get("/api/technicians");
  if (!techniciansResponse.ok()) {
    throw new Error(`Failed to load technicians: ${techniciansResponse.status()}`);
  }

  const technicians: Array<{ id: number }> = await techniciansResponse.json();
  const other = technicians.find((technician) => technician.id !== excludedTechnicianId);
  if (!other) {
    throw new Error(`No second technician found besides ${excludedTechnicianId}`);
  }

  return other.id;
}

export async function getTwoSlotsOnSameTechnician(page: Page): Promise<[ScheduleSlotContext, ScheduleSlotContext] | null> {
  const raw = await scheduleSlots(page).evaluateAll((elements) =>
    elements.map((el) => ({
      technicianId: Number(el.getAttribute("data-tech-id")),
      targetDate: el.getAttribute("data-date") ?? "",
      startMinutes: Number(el.getAttribute("data-slot-start-minutes")),
    }))
  );

  const valid = raw.filter(
    (s) => Number.isFinite(s.technicianId) && s.targetDate && s.startMinutes >= 8 * 60 && s.startMinutes + 30 <= 20 * 60
  );

  const byTech = new Map<string, typeof valid>();
  for (const s of valid) {
    const key = `${s.technicianId}|${s.targetDate}`;
    if (!byTech.has(key)) byTech.set(key, []);
    byTech.get(key)!.push(s);
  }

  const groups = [...byTech.values()]
    .map((slots) => [...slots].sort((left, right) => left.startMinutes - right.startMinutes))
    .sort((left, right) => left.length - right.length);

  const toContext = (s: (typeof valid)[0]): ScheduleSlotContext => ({
    technicianId: s.technicianId,
    targetDate: s.targetDate,
    startMinutes: s.startMinutes,
    endMinutes: s.startMinutes + 30,
  });

  for (const slots of groups) {
    for (let index = 0; index < slots.length - 1; index += 1) {
      const current = slots[index];
      const next = slots[index + 1];
      if (next.startMinutes - current.startMinutes <= 120) {
        return [toContext(current), toContext(next)];
      }
    }
  }

  const fallback = groups.find((slots) => slots.length >= 2);
  if (!fallback) return null;
  return [toContext(fallback[0]), toContext(fallback[1])];
}

export async function getAnotherVisibleSlotOnSameTechnician(
  page: Page,
  sourceSlot: ScheduleSlotContext
): Promise<ScheduleSlotContext | null> {
  const raw = await scheduleSlots(page).evaluateAll((elements) =>
    elements.map((el) => ({
      technicianId: Number(el.getAttribute("data-tech-id")),
      targetDate: el.getAttribute("data-date") ?? "",
      startMinutes: Number(el.getAttribute("data-slot-start-minutes")),
    }))
  );

  const candidates = raw
    .filter((slot) => slot.technicianId === sourceSlot.technicianId)
    .filter((slot) => slot.targetDate === sourceSlot.targetDate)
    .filter((slot) => slot.startMinutes !== sourceSlot.startMinutes)
    .filter((slot) => slot.startMinutes >= 8 * 60 && slot.startMinutes + 30 <= 20 * 60)
    .sort((left, right) => Math.abs(left.startMinutes - sourceSlot.startMinutes) - Math.abs(right.startMinutes - sourceSlot.startMinutes));

  const selected = candidates[0];
  if (!selected) return null;

  return {
    technicianId: selected.technicianId,
    targetDate: selected.targetDate,
    startMinutes: selected.startMinutes,
    endMinutes: selected.startMinutes + 30,
  };
}

export async function getNextVisibleSlotOnSameTechnician(
  page: Page,
  sourceSlot: ScheduleSlotContext
): Promise<ScheduleSlotContext | null> {
  const raw = await scheduleSlots(page).evaluateAll((elements) =>
    elements.map((el) => ({
      technicianId: Number(el.getAttribute("data-tech-id")),
      targetDate: el.getAttribute("data-date") ?? "",
      startMinutes: Number(el.getAttribute("data-slot-start-minutes")),
    }))
  );

  const candidates = raw
    .filter((slot) => slot.technicianId === sourceSlot.technicianId)
    .filter((slot) => slot.targetDate === sourceSlot.targetDate)
    .filter((slot) => slot.startMinutes > sourceSlot.startMinutes)
    .filter((slot) => slot.startMinutes >= 8 * 60 && slot.startMinutes + 30 <= 20 * 60)
    .sort((left, right) => left.startMinutes - right.startMinutes);

  const selected = candidates[0];
  if (!selected) return null;

  return {
    technicianId: selected.technicianId,
    targetDate: selected.targetDate,
    startMinutes: selected.startMinutes,
    endMinutes: selected.startMinutes + 30,
  };
}

export async function getTaskDetailViaApi(page: Page, taskId: number): Promise<TaskDetailContext> {
  const response = await page.request.get(`/api/tasks/${taskId}`);
  if (!response.ok()) {
    throw new Error(`Failed to load task ${taskId}: ${response.status()}`);
  }

  const task = await response.json();
  return {
    taskId,
    version: Number(task.version),
    technicianId: Number(task.technicianId),
    targetDate: String(task.startAt).split("T")[0],
    startMinutes: parseMinutesFromDateTime(String(task.startAt)),
    endMinutes: parseMinutesFromDateTime(String(task.endAt)),
    status: Number(task.status),
    technicianComment: String(task.technicianComment ?? ""),
    planStatus: String(task.planStatus ?? ""),
    isEditable: Boolean(task.isEditable),
    isDeletable: Boolean(task.isDeletable),
  };
}

function isRetryableTaskCreationError(status: number, responseText: string, hasExplicitSchedule: boolean): boolean {
  if (status === 409) {
    return !hasExplicitSchedule;
  }

  if (status === 422) {
    return !hasExplicitSchedule && (/Nepakanka laiko kelionei|travel/i.test(responseText) || isPastPlanningValidation(responseText));
  }

  return status === 500 && /pk_task_evaluations|dbupdateconcurrencyexception|deadlock detected|transient failure|40P01/i.test(responseText);
}

export async function createTaskViaApi(
  page: Page,
  overrides: Record<string, unknown> = {},
  options?: ScheduleSelectionOptions
): Promise<number> {
  const hasExplicitSchedule =
    typeof overrides.technicianId === "number" ||
    typeof overrides.targetDate === "string" ||
    typeof overrides.startMinutes === "number" ||
    typeof overrides.endMinutes === "number";
  const minTargetDate = options?.minTargetDate ?? defaultPlanningDate();

  const maxAttempts = hasExplicitSchedule ? 6 : 12;
  let lastError: string | null = null;

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    const visibleSlot = await getVisibleScheduleSlot(page, {
      preferredCity: options?.preferredCity,
      slotIndex: (options?.slotIndex ?? 0) + attempt,
      minTargetDate,
    }).catch(() => null);
    const normalizedSlot = visibleSlot ? normalizeImplicitScheduleToSafeFuture(visibleSlot, { minTargetDate }) : null;

    const technicianId =
      typeof overrides.technicianId === "number"
        ? overrides.technicianId
        : normalizedSlot?.technicianId;
    const targetDate =
      typeof overrides.targetDate === "string"
        ? overrides.targetDate
        : normalizedSlot?.targetDate ?? minTargetDate;
    const startMinutes =
      typeof overrides.startMinutes === "number"
        ? overrides.startMinutes
        : normalizedSlot?.startMinutes ?? 1110;
    const endMinutes =
      typeof overrides.endMinutes === "number"
        ? overrides.endMinutes
        : normalizedSlot?.endMinutes ?? 1140;

    if (!technicianId) {
      throw new Error("No visible technician slot available in the rendered schedule");
    }

    const response = await page.request.post("/api/tasks", {
      data: {
        technicianId,
        title: "E2E Temp Task",
        targetDate,
        startMinutes,
        endMinutes,
        priority: 1,
        status: 0,
        skillLevel: "Basic",
        objectName: "Vilniaus prekybos centras",
        objectAddress: "Konstitucijos pr. 16, Vilnius",
        timezoneOffsetMinutes: 120,
        ...overrides,
      },
    });

    if (response.ok()) {
      const body = await response.json();
      return body.id ?? body.taskId;
    }

    const responseText = await response.text();
    lastError = `${response.status()} ${responseText}`;
    if (!isRetryableTaskCreationError(response.status(), responseText, hasExplicitSchedule)) {
      break;
    }

    await page.waitForTimeout(250 * (attempt + 1));
  }

  throw new Error(`Failed to create task via API: ${lastError ?? "unknown error"}`);
}

export async function deleteTaskViaApi(page: Page, taskId: number, version = 0): Promise<void> {
  await page.request.delete(`/api/tasks/${taskId}`, {
    data: { version, autoCompact: false, timezoneOffsetMinutes: 120 },
  });
}

export async function cleanupTaskViaApi(page: Page, taskId: number): Promise<void> {
  const taskResponse = await page.request.get(`/api/tasks/${taskId}`).catch(() => null);
  const attemptedVersions: number[] = [];

  if (taskResponse?.ok()) {
    const task = await taskResponse.json().catch(() => null);
    const currentVersion = Number(task?.version);
    if (Number.isFinite(currentVersion)) {
      attemptedVersions.push(currentVersion);
    }
  }

  for (let version = 0; version <= 10; version += 1) {
    if (!attemptedVersions.includes(version)) {
      attemptedVersions.push(version);
    }
  }

  for (const version of attemptedVersions) {
    const response = await page.request.delete(`/api/tasks/${taskId}`, {
      data: { version, autoCompact: false, timezoneOffsetMinutes: 120 },
    }).catch(() => null);

    if (!response || response.ok() || response.status() === 404) {
      break;
    }

    if (response.status() !== 409) {
      break;
    }
  }
}

export async function updateTaskAsTechnicianViaApi(
  page: Page,
  taskId: number,
  input: { status: number; comment?: string; version: number }
): Promise<{ status: number; body: unknown }> {
  const response = await page.request.patch(`/api/tasks/${taskId}/technician-update`, {
    data: {
      status: input.status,
      comment: input.comment ?? "",
      version: input.version,
      timezoneOffsetMinutes: 120,
    },
  });

  const contentType = response.headers()["content-type"] ?? "";
  const body = contentType.includes("application/json") ? await response.json().catch(() => null) : await response.text();
  return {
    status: response.status(),
    body,
  };
}

export async function getTaskScheduleContext(page: Page, taskId: number): Promise<TaskScheduleContext> {
  const task = await getTaskDetailViaApi(page, taskId);
  return {
    taskId,
    technicianId: task.technicianId,
    targetDate: task.targetDate,
    startMinutes: task.startMinutes,
    endMinutes: task.endMinutes,
  };
}




