import type { Page } from "@playwright/test";
import { navNextBtn, navPrevBtn } from "../fixtures/selectors";

export async function waitForDateInGrid(page: Page, date: string): Promise<void> {
  await page.waitForFunction(
    (targetDate) =>
      document.querySelector(`[data-date="${targetDate}"]`) !== null ||
      document.querySelector(`[data-target-date="${targetDate}"]`) !== null ||
      window.location.search.includes(targetDate),
    date,
    { timeout: 15_000 }
  );
}

export async function waitForView(page: Page, view: string): Promise<void> {
  await page.waitForFunction(
    (targetView) => document.getElementById("scheduleTables")?.dataset?.currentView === targetView,
    view,
    { timeout: 15_000 }
  );
}

export async function goToNextSchedulePeriod(page: Page): Promise<string> {
  const nextBtn = navNextBtn(page);
  const targetDate = await nextBtn.getAttribute("data-target-date");
  if (!targetDate) {
    throw new Error("Next navigation button did not expose a target date.");
  }

  await nextBtn.click();
  await waitForDateInGrid(page, targetDate);
  return targetDate;
}

export async function goToPreviousSchedulePeriod(page: Page): Promise<string> {
  const prevBtn = navPrevBtn(page);
  const targetDate = await prevBtn.getAttribute("data-target-date");
  if (!targetDate) {
    throw new Error("Previous navigation button did not expose a target date.");
  }

  await prevBtn.click();
  await waitForDateInGrid(page, targetDate);
  return targetDate;
}
