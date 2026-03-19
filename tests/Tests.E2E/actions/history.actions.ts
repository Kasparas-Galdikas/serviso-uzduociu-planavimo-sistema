import type { Page } from "@playwright/test";

export async function openHistoryPanel(page: Page): Promise<void> {
  await page.evaluate(() => {
    (window as { schedule?: { history?: { openHistoryPanel?: () => void } } }).schedule?.history?.openHistoryPanel?.();
  });

  await page.waitForFunction(() => {
    const element = document.getElementById("taskDetailModal");
    return element && element.classList.contains("show");
  }, { timeout: 10_000 });

  await page.locator("#historyPanelContent").waitFor({ state: "visible", timeout: 10_000 });
  await page.waitForFunction(() => {
    const items = document.querySelectorAll(".schedule-history-item").length > 0;
    const empty = document.getElementById("scheduleHistoryEmpty")?.offsetParent !== null;
    return items || empty;
  }, { timeout: 10_000 });
}

export async function closeHistoryPanel(page: Page): Promise<void> {
  await page.evaluate(() => {
    (window as { schedule?: { panel?: { hide?: () => void } } }).schedule?.panel?.hide?.();
  });

  await page.waitForFunction(() => {
    const element = document.getElementById("taskDetailModal");
    return !element || !element.classList.contains("show");
  }, { timeout: 5_000 });
}

export async function expandFirstHistoryGroup(page: Page): Promise<void> {
  const group = page.locator(".schedule-history-group").first();
  const isExpanded = await group.locator(".collapse.show").count() > 0;
  if (!isExpanded) {
    await group.locator(".schedule-history-group__trigger").click();
    await page.waitForTimeout(500);
  }
}

export async function expandFirstSuccessHistorySection(page: Page): Promise<void> {
  const successSection = page.locator(".schedule-history-subsection").filter({
    has: page.locator(".bi-check-circle-fill"),
  }).first();
  const successCollapse = successSection.locator(".collapse");
  const isExpanded = await successCollapse.evaluate((element) => element.classList.contains("show"));
  if (!isExpanded) {
    await successSection.locator(".schedule-history-subsection__trigger").click();
    await page.waitForTimeout(500);
  }
}
