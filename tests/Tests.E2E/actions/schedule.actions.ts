import type { Locator, Page } from "@playwright/test";
import { expect } from "@playwright/test";
import { filterConsole, scheduleGrid, taskCards, taskModal } from "../fixtures/selectors";
import { today } from "../support/date";

export async function openSchedule(page: Page): Promise<void> {
  await page.goto("/Schedule");
}

export async function openDaySchedule(page: Page, date = today()): Promise<void> {
  await page.goto(`/Schedule?view=day&date=${date}`);
}

export async function openTechnicianDaySchedule(page: Page, technicianId: number, date = today()): Promise<void> {
  await page.goto(`/Schedule?view=day&date=${date}&technicianId=${technicianId}`);
}

export async function openWeekSchedule(page: Page, date = today()): Promise<void> {
  await page.goto(`/Schedule?view=week&date=${date}`);
}

export async function waitForScheduleGrid(page: Page): Promise<void> {
  await expect(scheduleGrid(page)).toBeVisible();
}

export async function openFirstTaskCard(page: Page): Promise<Locator> {
  const cards = taskCards(page);
  const count = await cards.count();
  if (count === 0) {
    throw new Error("No task cards were present in the seeded environment.");
  }

  const card = cards.first();
  await card.click();
  return card;
}

export async function openTaskCardById(page: Page, taskId: number): Promise<Locator> {
  const card = page.locator(`.task-card[data-task-id="${taskId}"]`);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.click();
  return card;
}

export async function openTaskCardByTitle(page: Page, title: string): Promise<Locator> {
  const card = taskCards(page).filter({ hasText: title }).first();
  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.click();
  return card;
}

export async function waitForTaskModal(page: Page): Promise<void> {
  await expect(taskModal(page)).toBeVisible();
}

export async function selectFirstAlternateFilterValue(page: Page): Promise<string | null> {
  const filters = filterConsole(page);
  await expect(filters).toBeVisible();

  const select = filters.locator("select").first();
  if (!(await select.count())) {
    return null;
  }

  const options = select.locator("option");
  const optionCount = await options.count();
  if (optionCount < 2) {
    return null;
  }

  const currentValue = await select.inputValue();
  for (let index = 0; index < optionCount; index += 1) {
    const value = await options.nth(index).getAttribute("value");
    if (value && value != currentValue) {
      await select.selectOption(value);
      return value;
    }
  }

  return null;
}

export async function fillFirstFilterInput(page: Page, value: string): Promise<boolean> {
  const filters = filterConsole(page);
  await expect(filters).toBeVisible();

  const input = filters.locator('input:not([type="hidden"])').first();
  if (!(await input.count())) {
    return false;
  }

  await input.fill(value);
  return true;
}
