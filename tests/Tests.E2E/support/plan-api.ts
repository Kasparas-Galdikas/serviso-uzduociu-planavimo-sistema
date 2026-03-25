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
  const response = await page.request.get("/api/plans/state");
  if (!response.ok() || !isJsonResponse(response)) {
    return;
  }

  const state = await response.json().catch(() => null);
  if (state?.draftPlanId) {
    await page.request.post("/api/plans/discard");
  }
}

export async function createDraftViaApi(page: Page): Promise<DraftPlanContext> {
  await page.goto("/Schedule");
  const minAttr = await page.locator("#createStartDate").getAttribute("min").catch(() => null);
  const startDate = minAttr || today();
  const endDate = addDays(startDate, 13);

  const maxAttempts = 4;
  let lastError = "unknown error";

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    await discardDraftIfExists(page);

    const response = await page.request.post("/api/plans/draft", {
      data: { startDate, endDate, weeks: 2 },
    });

    if (response.ok()) {
      return { startDate, endDate };
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
