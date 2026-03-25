import type { Page } from "@playwright/test";
import {
  clickCreatePlan,
  confirmPlanSummary,
  continuePlanCreation,
  discardDraft,
  publishDraft,
  setCreatePlanEndDate,
} from "../actions/plan.actions";
import { addDays, today } from "../support/date";
import { createDraftViaApi, discardDraftIfExists } from "../support/plan-api";

export async function openManagerScheduleWithoutDraft(page: Page): Promise<void> {
  await discardDraftIfExists(page);
  await page.goto("/Schedule");
}

export async function createDraftThroughUi(page: Page): Promise<{ startDate: string; endDate: string }> {
  const minAttr = await page.locator("#createStartDate").getAttribute("min");
  const startDate = minAttr || today();
  const endDate = addDays(startDate, 13);

  await clickCreatePlan(page);
  await setCreatePlanEndDate(page, endDate);
  await continuePlanCreation(page);
  await confirmPlanSummary(page);

  return { startDate, endDate };
}

export async function createDraftAndOpenWeekPreview(page: Page): Promise<{ startDate: string; endDate: string }> {
  await discardDraftIfExists(page);
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=week&date=${draft.startDate}`);
  return draft;
}

export async function openDraftWeekPreview(page: Page): Promise<{ startDate: string; endDate: string }> {
  return await createDraftAndOpenWeekPreview(page);
}

export async function createDraftAndOpenDayView(page: Page, date = today()): Promise<{ startDate: string; endDate: string; date: string }> {
  await discardDraftIfExists(page);
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=day&date=${date}`);
  return { ...draft, date };
}

export async function publishDraftThroughUi(page: Page): Promise<void> {
  await publishDraft(page);
}

export async function discardDraftThroughUi(page: Page): Promise<void> {
  await discardDraft(page);
}

export async function publishCurrentDraftAndWaitForSchedule(page: Page): Promise<void> {
  await publishDraftThroughUi(page);
  await page.waitForURL(/Schedule/i, { timeout: 20_000 });
}

export async function discardCurrentDraftAndWaitForSchedule(page: Page): Promise<void> {
  await discardDraftThroughUi(page);
  await page.waitForURL(/Schedule/i, { timeout: 20_000 });
}