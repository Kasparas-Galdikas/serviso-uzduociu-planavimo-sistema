import { expect, type Page } from "@playwright/test";
import {
  backlogAutoModeRadio,
  backlogBulkScheduleBtn,
  backlogNextBtn,
  backlogPanel,
  backlogSelectToggle,
  discardPlanBtn,
  publishPlanBtn,
} from "../fixtures/selectors";

export async function openBacklogPlanningPanel(page: Page): Promise<void> {
  await page.evaluate(() => {
    window.schedule?.backlog?.openBacklogPanel?.();
  });

  await backlogPanel(page).waitFor({ state: "visible", timeout: 10_000 });
}

export async function enterBacklogSelectionMode(page: Page): Promise<void> {
  await backlogSelectToggle(page).click();
}

export async function startBulkPlanning(page: Page): Promise<void> {
  await backlogBulkScheduleBtn(page).click();
}

export async function selectAutomaticPlanningMode(page: Page): Promise<void> {
  await page.locator('label[for="taskPlanModeAutomatinis"]').click();
  await expect(backlogAutoModeRadio(page)).toBeChecked();
}

export async function continueBulkPlanning(page: Page): Promise<void> {
  await backlogNextBtn(page).click();
}

export async function publishDraft(page: Page): Promise<void> {
  await publishPlanBtn(page).click();
}

export async function discardDraft(page: Page): Promise<void> {
  await discardPlanBtn(page).click();
}
