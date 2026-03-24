import { expect, type Locator, type Page } from "@playwright/test";

export type SeededUserRole = "manager" | "technician";

type SeededUser = {
  role: SeededUserRole;
  email: string;
};

const SEEDED_USERS: Record<SeededUserRole, SeededUser> = {
  manager: {
    role: "manager",
    email: process.env.E2E_MANAGER_EMAIL?.trim() || "manager@test.com"
  },
  technician: {
    role: "technician",
    email: process.env.E2E_TECHNICIAN_EMAIL?.trim() || "technician@test.com"
  }
};

const EMAIL_CANDIDATES = [
  'input[name="email"]',
  'input[type="email"]',
  'input[type="text"]'
];

const SUBMIT_CANDIDATES = [
  'button[type="submit"]',
  'input[type="submit"]',
  'button:has-text("Login")',
  'button:has-text("Log in")',
  'button:has-text("Prisijungti")'
];

export function getSeededUser(role: SeededUserRole): SeededUser {
  const overrideEmail = process.env.E2E_EMAIL?.trim() || process.env.E2E_USERNAME?.trim();
  if (overrideEmail) {
    return {
      role,
      email: overrideEmail
    };
  }

  return SEEDED_USERS[role];
}

async function firstVisible(page: Page, selectors: string[], description: string): Promise<Locator> {
  for (const selector of selectors) {
    const locator = page.locator(selector).first();
    if (await locator.isVisible().catch(() => false)) {
      return locator;
    }
  }

  for (const selector of selectors) {
    const locator = page.locator(selector).first();
    if (await locator.count()) {
      return locator;
    }
  }

  throw new Error(`Could not find ${description}. Tried selectors: ${selectors.join(", ")}`);
}

export async function loginAsRole(page: Page, role: SeededUserRole): Promise<void> {
  const user = getSeededUser(role);
  const maxAttempts = 8;

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    await page.goto("/");

    const startupErrorVisible = await page
      .getByText("An unhandled exception occurred while processing the request.")
      .isVisible()
      .catch(() => false);

    if (startupErrorVisible) {
      await page.waitForTimeout(2_000 * (attempt + 1));
      continue;
    }

    if (!page.url().includes("/Authentication")) {
      return;
    }

    const emailInput = await firstVisible(page, EMAIL_CANDIDATES, `${role} email input`);
    const submitButton = await firstVisible(page, SUBMIT_CANDIDATES, "login submit action");

    await emailInput.fill(user.email);
    await submitButton.click();

    try {
      await expect(page).not.toHaveURL(/Authentication/i, { timeout: 10_000 });
      return;
    } catch {
      const loginPageErrored = await page
        .getByText("An unhandled exception occurred while processing the request.")
        .isVisible()
        .catch(() => false);

      if (loginPageErrored || attempt < maxAttempts - 1) {
        await page.waitForTimeout(2_000 * (attempt + 1));
        continue;
      }

      throw new Error(`Failed to authenticate as ${role} after ${maxAttempts} attempts.`);
    }
  }

  throw new Error(`Failed to authenticate as ${role} after ${maxAttempts} attempts.`);
}

export async function loginAsManager(page: Page): Promise<void> {
  await loginAsRole(page, "manager");
}

export async function loginAsTechnician(page: Page): Promise<void> {
  await loginAsRole(page, "technician");
}

export async function ensureAuthenticated(page: Page): Promise<void> {
  await loginAsManager(page);
}


