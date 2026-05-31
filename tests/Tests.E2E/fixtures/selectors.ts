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
    '[data-schedule-filters="true"]',
    ".schedule-filter-bar",
    '[data-testid="schedule-filter-console"]',
    ".filter-console"
  ]);
}

export function taskCards(page: Page): Locator {
  return page.locator('.task-card[data-task-id]:not(.task-travel)');
}

export function taskModal(page: Page): Locator {
  return first(page, [
    '[data-testid="task-detail-modal"]',
    '[data-testid="task-modal"]',
    ".modal.show",
    '[role="dialog"]'
  ]);
}

// --- Plan lifecycle ---

export function draftBadge(page: Page): Locator {
  return first(page, [
    '.badge.bg-warning:has-text("Generavimo perziura")',
    '.badge.bg-warning:has-text("Juodrastis")'
  ]);
}

export function createPlanBtn(page: Page): Locator {
  return page.locator("#backlogPanelContent");
}

export function createPlanEndDateInput(page: Page): Locator {
  return page.locator("#backlogDueDate");
}

export function createPlanNextBtn(page: Page): Locator {
  return page.locator("#taskBulkNext");
}

export function summaryConfirmBtn(page: Page): Locator {
  return page.locator("#taskBulkNext");
}

export function discardPlanBtn(page: Page): Locator {
  return page.locator('#discardPlanBtn');
}

export function publishPlanBtn(page: Page): Locator {
  return page.locator('#publishPlanBtn');
}

export function previewBanner(page: Page): Locator {
  return first(page, [
    "#draftModeBanner",
    '.alert-warning:has-text("planavimo")',
    '.alert-warning:has-text("rezimas")'
  ]);
}

export function backlogPanel(page: Page): Locator {
  return page.locator("#backlogPanelContent");
}

export function backlogSelectToggle(page: Page): Locator {
  return page.locator("#backlogSelectToggle");
}

export function backlogBulkScheduleBtn(page: Page): Locator {
  return page.locator("#backlogBulkSchedule");
}

export function backlogAutoModeRadio(page: Page): Locator {
  return page.locator("#taskPlanModeAutomatinis");
}

export function backlogManualModeRadio(page: Page): Locator {
  return page.locator("#taskPlanModeRankinis");
}

export function backlogNextBtn(page: Page): Locator {
  return page.locator("#taskBulkNext");
}

export function backlogBackBtn(page: Page): Locator {
  return page.locator("#taskBulkBack");
}

export function backlogItemsList(page: Page): Locator {
  return page.locator("#backlogItemsList");
}

// --- Task panel ---

export function taskPanel(page: Page): Locator {
  return page.locator('#taskDetailModal');
}

export function taskTitleInput(page: Page): Locator {
  return page.locator('#taskDetailTitle');
}

export function taskStartInput(page: Page): Locator {
  return page.locator('#taskDetailStart');
}

export function taskEndInput(page: Page): Locator {
  return page.locator('#taskDetailEnd');
}

export function taskSaveBtn(page: Page): Locator {
  return first(page, [
    'button[data-panel-footer-action="task-save"]:not(.d-none)',
    '#taskDetailFooter #taskDetailSave:not(.d-none)',
    '#taskDetailSave:not(.d-none)'
  ]);
}

export function taskDeleteModalConfirm(page: Page): Locator {
  return page.locator('#taskDeleteConfirm');
}

export function taskStatusSelect(page: Page): Locator {
  return page.locator('#taskDetailStatus');
}

export function taskTechnicianCommentInput(page: Page): Locator {
  return page.locator('#taskDetailTechnicianComment');
}

export function taskReadonlyNote(page: Page): Locator {
  return page.locator('#taskDetailReadonlyNote');
}

// --- Schedule slots (empty cells) ---

export function scheduleSlots(page: Page): Locator {
  return page.locator('.schedule-slot[data-tech-id][data-date][data-slot-start-minutes]');
}

// --- Edit mode ---

export function editModeToggle(page: Page): Locator {
  return page.locator("[data-edit-toggle]").first();
}

// --- Navigation ---

export function navPrevBtn(page: Page): Locator {
  return page.locator('[data-nav="prev"]').first();
}

export function navNextBtn(page: Page): Locator {
  return page.locator('[data-nav="next"]').first();
}

// --- History panel ---

export function historyList(page: Page): Locator {
  return page.locator('#scheduleHistoryList');
}

export function historyItems(page: Page): Locator {
  return page.locator('.schedule-history-item');
}

export function historyEmptyState(page: Page): Locator {
  return page.locator('#scheduleHistoryEmpty');
}

// --- Task panel extra fields ---

export function taskPrioritySelect(page: Page): Locator {
  return page.locator('#taskDetailPriority');
}

export function taskSkillSelect(page: Page): Locator {
  return page.locator('#taskDetailSkill');
}

export function taskInstructionsInput(page: Page): Locator {
  return page.locator('#taskDetailInstructions');
}

// --- Resize overlay ---

export function taskResizeOverlay(page: Page): Locator {
  return page.locator('.slot-selection-bar.task-resize-bar');
}

export function taskResizeRightHandle(page: Page): Locator {
  return page.locator('.slot-selection-bar.task-resize-bar [data-slot-handle="right"]');
}

// --- Toasts ---

export function toasts(page: Page): Locator {
  return page.locator('.schedule-toast');
}

export function successToasts(page: Page): Locator {
  return page.locator('.schedule-toast--success');
}

export function errorToasts(page: Page): Locator {
  return page.locator('.schedule-toast--error');
}

export function warningToasts(page: Page): Locator {
  return page.locator('.schedule-toast--warning');
}
