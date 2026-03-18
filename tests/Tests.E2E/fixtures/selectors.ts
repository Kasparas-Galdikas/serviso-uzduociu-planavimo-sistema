import type { Locator, Page } from "@playwright/test";

function first(page: Page, selectors: string[]): Locator {
  return page.locator(selectors.join(", ")).first();
}

export function schedulePage(page: Page): Locator {
  return first(page, [
    '[data-testid="schedule-page"]',
    '[data-page="schedule"]',
    "main",
    "body"
  ]);
}

export function scheduleGrid(page: Page): Locator {
  return first(page, [
    '[data-testid="schedule-grid"]',
    '[data-testid="schedule-table"]',
    '[data-schedule-grid]',
    ".schedule-grid",
    ".schedule-table",
    "table"
  ]);
}

export function filterConsole(page: Page): Locator {
  return first(page, [
    '[data-testid="schedule-filter-console"]',
    '[data-testid="filter-console"]',
    ".filter-console",
    "form"
  ]);
}

export function taskCards(page: Page): Locator {
  return page.locator(
    [
      '[data-testid="task-card"]',
      '[data-task-id]',
      ".task-card",
      '[draggable="true"]'
    ].join(", ")
  );
}

export function taskModal(page: Page): Locator {
  return first(page, [
    '[data-testid="task-detail-modal"]',
    '[data-testid="task-modal"]',
    ".modal.show",
    '[role="dialog"]'
  ]);
}
