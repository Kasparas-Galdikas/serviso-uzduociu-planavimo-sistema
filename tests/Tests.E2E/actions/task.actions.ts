import type { Locator, Page } from "@playwright/test";
import { expect } from "@playwright/test";
import { scheduleSlots, taskPanel, taskSaveBtn, taskStatusSelect, taskTechnicianCommentInput, taskTitleInput } from "../fixtures/selectors";

export type TaskFormInput = {
  title: string;
  skill?: string;
  priority?: string;
};

export type SlotSelectionContext = {
  technicianId: number;
  targetDate: string;
  startMinutes: number;
  endMinutes: number;
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

export async function waitForTaskPanelToClose(page: Page): Promise<void> {
  await expect(taskPanel(page)).not.toHaveClass(/show/, { timeout: 30_000 });
}

export async function openFirstScheduleSlot(page: Page): Promise<SlotSelectionContext> {
  const slots = scheduleSlots(page);
  const count = await slots.count();
  if (count === 0) {
    throw new Error("No empty schedule slots on the rendered schedule.");
  }

  const slot = slots.first();
  const slotContext = await slot.evaluate((element) => {
    const technicianId = Number(element.getAttribute("data-tech-id"));
    const targetDate = element.getAttribute("data-date") ?? "";
    const startMinutes = Number(element.getAttribute("data-slot-start-minutes"));
    return {
      technicianId,
      targetDate,
      startMinutes,
      endMinutes: startMinutes + 30,
    };
  });

  await slot.click();
  return slotContext;
}

export async function chooseCreateTaskFromSlot(page: Page, slotContext: SlotSelectionContext): Promise<void> {
  const selectionHit = page.locator(".slot-selection-bar [data-slot-hit]").first();
  await expect(selectionHit).toBeVisible({ timeout: 5_000 });

  // Draft task creation reuses the same offcanvas shell as backlog planning.
  // If an old backlog scheduling context is still hanging around, the real UI
  // path will legitimately switch the shell back to backlog-create. Clear only
  // that stale context, then use the same second-click behavior as a user.
  await page.evaluate(() => {
    window.schedule?.backlog?.state?.clearSchedulingContext?.();
  });

  await selectionHit.click();

  await page.waitForFunction(() => {
    const panel = document.getElementById("taskDetailModal");
    return panel?.classList.contains("show") &&
      panel.dataset.panelVariant === "task-create" &&
      panel.dataset.mode === "create";
  }, { timeout: 10_000 });

  await page.waitForFunction((selection) => {
    const panel = document.getElementById("taskDetailModal");
    return panel?.dataset.technicianId === String(selection.technicianId) &&
      panel?.dataset.targetDate === selection.targetDate &&
      panel?.dataset.startMinutes === String(selection.startMinutes) &&
      panel?.dataset.endMinutes === String(selection.endMinutes);
  }, slotContext, { timeout: 10_000 });
}

async function selectFirstNonEmptyOption(locator: Locator): Promise<string> {
  const value = await locator.locator("option").evaluateAll((options) => {
    const match = options.find((option) => option.getAttribute("value"));
    return match?.getAttribute("value") ?? "";
  });

  if (!value) {
    throw new Error("No selectable option available");
  }

  await selectOptionAndWait(locator, value);
  return value;
}

async function selectOptionAndWait(locator: Locator, value: string): Promise<void> {
  await locator.selectOption(value);
  await expect(locator).toHaveValue(value);
}

export async function fillTaskForm(page: Page, input: TaskFormInput): Promise<void> {
  await taskTitleInput(page).fill(input.title);
  await selectOptionAndWait(page.locator("#taskDetailSkill"), input.skill ?? "Basic");
  await selectOptionAndWait(page.locator("#taskDetailPriority"), input.priority ?? "1");
  await selectFirstNonEmptyOption(page.locator("#taskDetailObject"));

  const location = page.locator("#taskDetailLocation");
  if (!(await location.inputValue())) {
    await selectFirstNonEmptyOption(location);
  }
}

export async function saveTask(page: Page): Promise<void> {
  await taskSaveBtn(page).click();
}

export async function saveTaskAndWaitForCreateResult(
  page: Page,
): Promise<{ ok: boolean; status: number; id: number | null; body: unknown }> {
  const responsePromise = page.waitForResponse((resp) => {
    if (resp.request().method() !== "POST") {
      return false;
    }

    try {
      const pathname = new URL(resp.url()).pathname;
      return pathname === "/api/tasks" || pathname === "/api/tasks/draft";
    } catch {
      return false;
    }
  }, { timeout: 15_000 });

  await saveTask(page);

  const response = await responsePromise;
  const body = await response.json().catch(() => null);
  const id = typeof body?.id === "number"
    ? body.id
    : typeof body?.taskId === "number"
      ? body.taskId
      : null;

  return {
    ok: response.ok(),
    status: response.status(),
    id,
    body,
  };
}

export async function saveTaskAndWaitForClose(page: Page): Promise<void> {
  await saveTask(page);
  await waitForTaskPanelToClose(page);
}

export async function saveTaskAndWaitForVersionChange(page: Page): Promise<void> {
  await page.waitForFunction(() => {
    const modal = document.getElementById("taskDetailModal");
    return modal?.dataset.mode === "update" && !!modal?.dataset.taskId;
  }, { timeout: 10_000 });

  // Dismiss the filter console modal if it somehow opened (it would block the save button via its backdrop).
  const filterModal = page.locator("#filterConsoleModal.show");
  if (await filterModal.isVisible()) {
    await page.locator("#filterConsoleModal [data-bs-dismiss='modal']").click();
    await filterModal.waitFor({ state: "hidden", timeout: 5_000 });
  }

  const responsePromise = page.waitForResponse(
    (resp) => /\/api\/tasks\/\d+/.test(resp.url()) &&
      (resp.request().method() === "PATCH" || resp.request().method() === "PUT"),
    { timeout: 15_000 }
  );

  // Use evaluate to dispatch the click directly on the element, bypassing any z-index overlays.
  await page.evaluate(() => {
    document
      .querySelector('button[data-panel-footer-action="task-save"]:not(.d-none), #taskDetailFooter #taskDetailSave:not(.d-none)')
      ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });

  await responsePromise;
}

export async function confirmTaskDeletion(page: Page): Promise<void> {
  await page.locator("#taskDeleteConfirm").click();
}

export async function openTaskDeleteConfirmation(card: Locator, page: Page): Promise<void> {
  await card.hover();
  const deleteBtn = card.locator(".task-card-action-delete");
  await expect(deleteBtn).toBeVisible({ timeout: 5_000 });

  // Retrieve the task ID so we can click the button via evaluate, which dispatches the event
  // directly to the DOM element and bypasses any z-index overlays (e.g. the task detail offcanvas
  // that may auto-open after task creation in Firefox).
  const taskId = await card.getAttribute("data-task-id");
  await page.evaluate((tid) => {
    const btn = document.querySelector(
      `.task-card[data-task-id="${CSS.escape(String(tid ?? ""))}"] .task-card-action-delete`
    ) as HTMLElement | null;
    btn?.click();
  }, taskId);

  await page.waitForFunction(() => {
    const modal = document.getElementById("taskDeleteModal");
    return !!modal && modal.classList.contains("show");
  }, { timeout: 30_000 });
}

export async function reopenTaskCard(page: Page, card: Locator): Promise<void> {
  await card.click();
  await waitForTaskPanel(page);
}

export async function editPanelPriority(page: Page, value: string): Promise<void> {
  await selectOptionAndWait(page.locator("#taskDetailPriority"), value);
}

export async function editPanelSkill(page: Page, value: string): Promise<void> {
  await selectOptionAndWait(page.locator("#taskDetailSkill"), value);
}

export async function editPanelInstructions(page: Page, text: string): Promise<void> {
  const section = page.locator("#taskSecSafety");
  const isExpanded = await section.evaluate((el) => el.classList.contains("show"));
  if (!isExpanded) {
    await page.locator('#taskDetailModal [data-bs-target="#taskSecSafety"]').first().click();
    await section.waitFor({ state: "visible", timeout: 3_000 });
  }
  await page.locator("#taskDetailInstructions").fill(text);
}

export async function editTechnicianStatus(page: Page, value: string): Promise<void> {
  await selectOptionAndWait(taskStatusSelect(page), value);
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
