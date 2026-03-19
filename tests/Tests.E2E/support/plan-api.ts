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

  const response = await page.request.post("/api/plans/draft", {
    data: { startDate, endDate, weeks: 2 },
  });

  if (!response.ok()) {
    throw new Error(`Failed to create draft via API: ${response.status()} ${await response.text()}`);
  }

  return { startDate, endDate };
}
