import type { Page } from "@playwright/test";

export type CurrentUserContext = {
  role: string;
  userId: number;
  technicianId: number | null;
};

export async function getCurrentUserContext(page: Page): Promise<CurrentUserContext> {
  const response = await page.request.get("/api/auth/me");
  if (!response.ok()) {
    throw new Error(`Failed to load current user context: ${response.status()}`);
  }

  const body = await response.json();
  return {
    role: String(body.role ?? ""),
    userId: Number(body.userId),
    technicianId: body.technicianId == null ? null : Number(body.technicianId),
  };
}
