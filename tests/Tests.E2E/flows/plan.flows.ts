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
import { createDraftViaApi } from "../support/plan-api";

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

export async function openDraftWeekPreview(page: Page): Promise<{ startDate: string; endDate: string }> {
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=week&date=${draft.startDate}`);
  return draft;
}

export async function publishDraftThroughUi(page: Page): Promise<void> {
  await publishDraft(page);
}

export async function discardDraftThroughUi(page: Page): Promise<void> {
  await discardDraft(page);
}
