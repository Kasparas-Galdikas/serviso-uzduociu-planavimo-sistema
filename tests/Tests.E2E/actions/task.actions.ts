import type { Locator, Page } from "@playwright/test";
import { expect } from "@playwright/test";
import { scheduleSlots, taskPanel, taskSaveBtn, taskStatusSelect, taskTechnicianCommentInput, taskTitleInput } from "../fixtures/selectors";

export type TaskFormInput = {
  title: string;
  skill?: string;
  priority?: string;
};

export async function waitForTaskPanel(page: Page): Promise<void> {
  await page.waitForFunction(() => document.getElementById("taskDetailModal") !== null, { timeout: 5_000 });

  await page.waitForFunction(() => {
    const el = document.getElementById("taskDetailModal");
    if (!el) {
      return false;
    }

    if (el.classList.contains("show")) {
      return true;
    }

    const style = window.getComputedStyle(el);
    return style.display !== "none" && style.visibility !== "hidden" && style.opacity !== "0";
  }, { timeout: 20_000 });

  await taskPanel(page).waitFor({ state: "visible", timeout: 5_000 });
}

export async function openFirstScheduleSlot(page: Page): Promise<void> {
  const slots = scheduleSlots(page);
  const count = await slots.count();
  if (count === 0) {
    throw new Error("No empty schedule slots on the rendered schedule.");
  }

  await slots.first().click();
}

export async function chooseCreateTaskFromSlot(page: Page): Promise<void> {
  const selectionAction = page.getByRole("button", { name: /Veiksmai: kurti uzduoti arba priskirti/i }).first();
  await selectionAction.click();
}

async function selectFirstNonEmptyOption(locator: Locator): Promise<string> {
  const value = await locator.locator("option").evaluateAll((options) => {
    const match = options.find((option) => option.getAttribute("value"));
    return match?.getAttribute("value") ?? "";
  });

  if (!value) {
    throw new Error("No selectable option available");
  }

  await locator.selectOption(value);
  return value;
}

export async function fillTaskForm(page: Page, input: TaskFormInput): Promise<void> {
  await taskTitleInput(page).fill(input.title);
  await page.locator("#taskDetailSkill").selectOption(input.skill ?? "Basic");
  await page.locator("#taskDetailPriority").selectOption(input.priority ?? "1");
  await selectFirstNonEmptyOption(page.locator("#taskDetailObject"));

  const location = page.locator("#taskDetailLocation");
  if (!(await location.inputValue())) {
    await selectFirstNonEmptyOption(location);
  }
}

export async function saveTask(page: Page): Promise<void> {
  await taskSaveBtn(page).click();
}

export async function confirmTaskDeletion(page: Page): Promise<void> {
  await page.locator("#taskDeleteConfirm").click();
}

export async function editPanelPriority(page: Page, value: string): Promise<void> {
  await page.locator("#taskDetailPriority").selectOption(value);
}

export async function editPanelSkill(page: Page, value: string): Promise<void> {
  await page.locator("#taskDetailSkill").selectOption(value);
}

export async function editPanelInstructions(page: Page, text: string): Promise<void> {
  const section = page.locator("#taskSecSafety");
  const isExpanded = await section.evaluate((el) => el.classList.contains("show"));
  if (!isExpanded) {
    await page.locator('[data-bs-target="#taskSecSafety"]').first().click();
    await section.waitFor({ state: "visible", timeout: 3_000 });
  }
  await page.locator("#taskDetailInstructions").fill(text);
}

export async function editTechnicianStatus(page: Page, value: string): Promise<void> {
  await taskStatusSelect(page).selectOption(value);
}

export async function editTechnicianComment(page: Page, text: string): Promise<void> {
  await taskTechnicianCommentInput(page).fill(text);
}

export async function activateResizeMode(card: Locator): Promise<void> {
  await card.hover();
  const resizeBtn = card.locator(".task-card-action-edit");
  await expect(resizeBtn).toBeVisible({ timeout: 5_000 });
  await resizeBtn.click();
}