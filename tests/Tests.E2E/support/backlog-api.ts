import type { Page } from "@playwright/test";

export type BacklogItemInput = {
  title: string;
  priority?: number;
  itemType?: number;
  skillLevel?: string;
  objectName?: string;
  objectAddress?: string;
  instructions?: string;
  dueDate?: string | null;
};

export async function createBacklogItemViaApi(
  page: Page,
  input: BacklogItemInput
): Promise<number> {
  const response = await page.request.post("/api/backlog", {
    data: {
      title: input.title,
      priority: input.priority ?? 2,
      itemType: input.itemType ?? 1,
      skillLevel: input.skillLevel ?? "Advanced",
      objectId: null,
      objectName: input.objectName ?? "Vilniaus prekybos centras",
      objectAddress: input.objectAddress ?? "Konstitucijos pr. 16, Vilnius",
      instructions: input.instructions ?? "Backlog drag instructions",
      dueDate: input.dueDate ?? null,
    },
  });

  if (!response.ok()) {
    throw new Error(`Failed to create backlog item via API: ${response.status()} ${await response.text()}`);
  }

  const body = await response.json();
  return Number(body?.item?.id || 0);
}

export async function cleanupBacklogItemViaApi(page: Page, backlogItemId: number): Promise<void> {
  await page.request.delete(`/api/backlog/${backlogItemId}`).catch(() => null);
}
