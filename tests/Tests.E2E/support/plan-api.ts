import type { APIResponse, Page } from "@playwright/test";
import { addDays, today } from "./date";

export type DraftPlanContext = {
  startDate: string;
  endDate: string;
};

function isJsonResponse(response: APIResponse): boolean {
  const contentType = response.headers()["content-type"] ?? "";
  return contentType.toLowerCase().includes("application/json");
}

function isRetryableDraftCreationError(status: number, responseText: string): boolean {
  if (status === 409) {
    return true;
  }

  return status === 500 && /ix_plans_status_draft|transient failure|deadlock detected|40P01/i.test(responseText);
}

export async function discardDraftIfExists(page: Page): Promise<void> {
  const maxAttempts = 3;

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    try {
      const response = await page.request.get("/api/plans/state");
      if (!response.ok() || !isJsonResponse(response)) {
        return;
      }

      const state = await response.json().catch(() => null);
      if (!state?.draftPlanId) {
        return;
      }

      const discardResponse = await page.request.post("/api/plans/discard");
      if (discardResponse.ok() || discardResponse.status() === 404 || discardResponse.status() === 403) {
        return;
      }
    } catch {
      // Best-effort cleanup: transient draft cleanup failures should not fail the suite.
    }

    await page.waitForTimeout(250 * (attempt + 1));
  }
}

export async function createDraftViaApi(page: Page): Promise<DraftPlanContext> {
  await page.goto("/Schedule");
  const earliestDate = await page.locator("#scheduleTables").getAttribute("data-earliest-creatable-date").catch(() => null);
  const startDate = earliestDate || today();
  const endDate = addDays(startDate, 13);

  const maxAttempts = 4;
  let lastError = "unknown error";

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    await discardDraftIfExists(page);

    const response = await page.request.post("/api/plans/backlog-draft/ensure", {
      data: { startDate, endDate, weeks: 2 },
    });

    if (response.ok()) {
      const body = await response.json().catch(() => null);
      return {
        startDate: String(body?.periodStart || startDate),
        endDate: String(body?.periodEnd || endDate),
      };
    }

    const responseText = await response.text();
    lastError = `${response.status()} ${responseText}`;
    if (!isRetryableDraftCreationError(response.status(), responseText)) {
      break;
    }

    await page.waitForTimeout(250 * (attempt + 1));
  }

  throw new Error(`Failed to create draft via API: ${lastError}`);
}
