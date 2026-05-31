import type { Locator, Page, Route } from "@playwright/test";
import { authenticateAsManager } from "../actions/auth.actions";
import {
  closeHistoryPanel,
  expandFirstHistoryGroup,
  expandFirstSuccessHistorySection,
  openHistoryPanel,
} from "../actions/history.actions";
import { today } from "../support/date";

type PlanState = {
  activePlanId: number | null;
  draftPlanId: number | null;
};

type HistoryMutationContext = {
  targetDate?: string;
};

async function getCurrentPagePlanId(page: Page): Promise<number | null> {
  const rawPlanId = await page.locator("#scheduleTables").getAttribute("data-plan-id");
  if (!rawPlanId) {
    return null;
  }

  const planId = Number(rawPlanId);
  return Number.isFinite(planId) && planId > 0 ? planId : null;
}

async function waitForPlanHistoryEntries(page: Page, planId: number | null, minimumCount = 1): Promise<void> {
  if (planId === null) {
    return;
  }

  await page.waitForFunction(
    async ({ currentPlanId, expectedMinimumCount }) => {
      const response = await fetch(`/api/plans/${currentPlanId}/history?limit=50&includeBacklog=true`, {
        credentials: "same-origin",
      });
      if (!response.ok) {
        return false;
      }

      const entries = await response.json().catch(() => []);
      return Array.isArray(entries) && entries.length >= expectedMinimumCount;
    },
    { currentPlanId: planId, expectedMinimumCount: minimumCount },
    { timeout: 10_000 }
  );
}

async function openHistoryPanelForCurrentPagePlan(page: Page): Promise<void> {
  const currentPagePlanId = await getCurrentPagePlanId(page);
  if (currentPagePlanId === null) {
    await openHistoryPanel(page);
    return;
  }

  const stateRoutePattern = "**/api/plans/state";
  const stateOverride = async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        activePlanId: currentPagePlanId,
        draftPlanId: null,
      }),
    });
  };

  await page.route(stateRoutePattern, stateOverride);
  try {
    await openHistoryPanel(page);
  } finally {
    await page.unroute(stateRoutePattern, stateOverride);
  }
}

async function getPlanState(page: Page): Promise<PlanState> {
  const stateResponse = await page.request.get("/api/plans/state");
  if (!stateResponse.ok()) {
    return { activePlanId: null, draftPlanId: null };
  }

  const state = await stateResponse.json().catch(() => null);
  return {
    activePlanId: typeof state?.activePlanId === "number" ? state.activePlanId : null,
    draftPlanId: typeof state?.draftPlanId === "number" ? state.draftPlanId : null,
  };
}

async function getHistoryEntryCount(page: Page, planId: number | null): Promise<number> {
  if (planId === null) {
    return 0;
  }

  const response = await page.request.get(`/api/plans/${planId}/history?limit=50`);
  if (!response.ok()) {
    return 0;
  }

  const entries = await response.json().catch(() => []);
  return Array.isArray(entries) ? entries.length : 0;
}

export async function getCurrentPlanHistoryEntryCount(page: Page): Promise<number> {
  const state = await getPlanState(page);
  const [activeCount, draftCount] = await Promise.all([
    getHistoryEntryCount(page, state.activePlanId),
    getHistoryEntryCount(page, state.draftPlanId),
  ]);

  return Math.max(activeCount, draftCount);
}

export async function openManagerHistoryForToday(page: Page, date = today()): Promise<void> {
  await authenticateAsManager(page);
  await page.goto(`/Schedule?view=day&date=${date}`);
  await page.waitForLoadState("networkidle");
  await openHistoryPanel(page);
}

export async function openManagerHistoryAfterTaskMutation(
  page: Page,
  mutation: () => Promise<HistoryMutationContext | void>
): Promise<void> {
  const currentUrl = page.url();
  const beforeState = await getPlanState(page);
  const beforeActiveCount = await getHistoryEntryCount(page, beforeState.activePlanId);
  const beforeDraftCount = await getHistoryEntryCount(page, beforeState.draftPlanId);

  await closeHistoryPanel(page);
  const mutationContext = (await mutation()) ?? {};

  await page.waitForFunction(
    async ({ activePlanId, draftPlanId, activeCount, draftCount }) => {
      const stateResponse = await fetch("/api/plans/state", { credentials: "same-origin" });
      if (!stateResponse.ok) {
        return false;
      }

      const state = await stateResponse.json().catch(() => null);
      const currentActivePlanId = typeof state?.activePlanId === "number" ? state.activePlanId : null;
      const currentDraftPlanId = typeof state?.draftPlanId === "number" ? state.draftPlanId : null;

      const getCount = async (planId: number | null) => {
        if (planId === null) {
          return 0;
        }

        const response = await fetch(`/api/plans/${planId}/history?limit=50`, { credentials: "same-origin" });
        if (!response.ok) {
          return 0;
        }

        const entries = await response.json().catch(() => []);
        return Array.isArray(entries) ? entries.length : 0;
      };

      const currentActiveCount = await getCount(currentActivePlanId);
      const currentDraftCount = await getCount(currentDraftPlanId);

      return (
        currentActiveCount > activeCount ||
        currentDraftCount > draftCount ||
        (currentDraftPlanId !== null && currentDraftPlanId !== draftPlanId && currentDraftCount > 0) ||
        (currentActivePlanId !== null && currentActivePlanId !== activePlanId && currentActiveCount > 0)
      );
    },
    {
      activePlanId: beforeState.activePlanId,
      draftPlanId: beforeState.draftPlanId,
      activeCount: beforeActiveCount,
      draftCount: beforeDraftCount,
    },
    { timeout: 10_000 }
  ).catch(() => null);

  const afterState = await getPlanState(page);
  const targetUrl = mutationContext.targetDate
    ? `/Schedule?view=day&date=${mutationContext.targetDate}`
    : afterState.draftPlanId !== null
      ? "/Schedule"
      : currentUrl;

  await page.goto(targetUrl);
  await page.waitForLoadState("networkidle");
  const currentPagePlanId = await getCurrentPagePlanId(page);
  await waitForPlanHistoryEntries(page, currentPagePlanId).catch(() => null);
  await openHistoryPanelForCurrentPagePlan(page);
}

export async function expandFirstSuccessfulHistoryEntry(page: Page): Promise<Locator> {
  await page.waitForFunction(
    () =>
      document.querySelector(".schedule-history-group__trigger") !== null ||
      document.querySelector(".schedule-history-item.outcome-success") !== null,
    { timeout: 10_000 }
  );

  if ((await page.locator(".schedule-history-group__trigger").count()) > 0) {
    await expandFirstHistoryGroup(page);
  }

  if (
    (await page.locator(".schedule-history-subsection").filter({ has: page.locator(".bi-check-circle-fill") }).count()) > 0
  ) {
    await expandFirstSuccessHistorySection(page);
  }

  return page.locator(".schedule-history-item.outcome-success").first();
}
