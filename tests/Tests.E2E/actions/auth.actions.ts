import type { Page } from "@playwright/test";
import { ensureAuthenticated, loginAsManager, loginAsTechnician } from "../fixtures/auth";

export async function authenticate(page: Page): Promise<void> {
  await ensureAuthenticated(page);
}

export async function authenticateAsManager(page: Page): Promise<void> {
  await loginAsManager(page);
}

export async function authenticateAsTechnician(page: Page): Promise<void> {
  await loginAsTechnician(page);
}

export async function resetAuthentication(page: Page): Promise<void> {
  await page.context().clearCookies();
}

export async function switchToManager(page: Page): Promise<void> {
  await resetAuthentication(page);
  await loginAsManager(page);
}

export async function switchToTechnician(page: Page): Promise<void> {
  await resetAuthentication(page);
  await loginAsTechnician(page);
}
