import { expect, test } from "@playwright/test";

test.describe("authentication smoke", () => {
  test("redirects anonymous users to the login page without uncaught page errors", async ({ page }) => {
    const pageErrors: Error[] = [];
    const consoleErrors: string[] = [];

    page.on("pageerror", (error) => pageErrors.push(error));
    page.on("console", (message) => {
      if (message.type() === "error") {
        consoleErrors.push(message.text());
      }
    });

    await page.goto("/");

    await expect(page).toHaveURL(/Authentication/i);
    await expect(page.locator('input[type="password"]').first()).toBeVisible();
    expect(pageErrors, "page errors").toEqual([]);
    expect(consoleErrors, "console errors").toEqual([]);
  });
});
