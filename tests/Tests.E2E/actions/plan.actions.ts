import type { Page } from "@playwright/test";
import {
  createPlanBtn,
  createPlanEndDateInput,
  createPlanNextBtn,
  discardPlanBtn,
  publishPlanBtn,
  summaryConfirmBtn,
} from "../fixtures/selectors";

export async function clickCreatePlan(page: Page): Promise<void> {
  await createPlanBtn(page).click();
}

export async function setCreatePlanEndDate(page: Page, endDate: string): Promise<void> {
  await createPlanEndDateInput(page).fill(endDate);
}

export async function continuePlanCreation(page: Page): Promise<void> {
  await createPlanNextBtn(page).click();
}

export async function confirmPlanSummary(page: Page): Promise<void> {
  await summaryConfirmBtn(page).click();
}

export async function publishDraft(page: Page): Promise<void> {
  await publishPlanBtn(page).click();
}

export async function discardDraft(page: Page): Promise<void> {
  await discardPlanBtn(page).click();
}
