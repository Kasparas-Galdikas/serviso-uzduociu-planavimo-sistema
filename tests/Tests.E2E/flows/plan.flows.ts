import type { Page } from "@playwright/test";
import {
  continueBulkPlanning,
  discardDraft,
  enterBacklogSelectionMode,
  openBacklogPlanningPanel,
  publishDraft,
  selectAutomaticPlanningMode,
  startBulkPlanning,
} from "../actions/plan.actions";
import { createDraftViaApi, discardDraftIfExists } from "../support/plan-api";

export async function openManagerScheduleWithoutDraft(page: Page): Promise<void> {
  await discardDraftIfExists(page);
  await page.goto("/Schedule");
}

export async function openBacklogPlanningFlow(page: Page): Promise<void> {
  await openBacklogPlanningPanel(page);
  await enterBacklogSelectionMode(page);
}

export async function createDraftThroughUi(page: Page): Promise<{ startDate: string; endDate: string }> {
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=day&date=${draft.startDate}&planScope=draft`);
  return draft;
}

export async function startAutomaticDraftPlanningFromBacklog(page: Page): Promise<void> {
  await startBulkPlanning(page);
  await selectAutomaticPlanningMode(page);
}

export async function finishAutomaticDraftPlanning(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const nextButton = page.locator("#taskBulkNext");
    const nextVisible = await nextButton.isVisible().catch(() => false);
    if (!nextVisible) {
      break;
    }

    await continueBulkPlanning(page);
    await page.waitForTimeout(300);

    const stillInDraft = await page.locator("#draftModeBanner").isVisible().catch(() => false);
    if (!stillInDraft) {
      break;
    }
  }
}

export async function createDraftAndOpenWeekPreview(page: Page): Promise<{ startDate: string; endDate: string }> {
  await discardDraftIfExists(page);
  const draft = await createDraftViaApi(page);
  await page.goto(`/Schedule?view=week&date=${draft.startDate}&planScope=draft`);
  return draft;
}

export async function openDraftWeekPreview(page: Page): Promise<{ startDate: string; endDate: string }> {
  return await createDraftAndOpenWeekPreview(page);
}

export async function createDraftAndOpenDayView(page: Page, date?: string): Promise<{ startDate: string; endDate: string; date: string }> {
  await discardDraftIfExists(page);
  const draft = await createDraftViaApi(page);
  const targetDate = date ?? draft.startDate;
  await page.goto(`/Schedule?view=day&date=${targetDate}&planScope=draft`);
  return { ...draft, date: targetDate };
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
