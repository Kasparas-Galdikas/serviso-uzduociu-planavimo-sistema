import { expect, test } from "@playwright/test";
import { ensureAuthenticated } from "../fixtures/auth";
import { filterConsole, scheduleGrid, schedulePage, taskCards, taskModal } from "../fixtures/selectors";

test.describe("schedule smoke", () => {
  test.beforeEach(async ({ page }) => {
    await ensureAuthenticated(page);
    await page.goto("/Schedule");
  });

  test("loads the schedule shell and grid", async ({ page }) => {
    await expect(schedulePage(page)).toBeVisible();
    await expect(scheduleGrid(page)).toBeVisible();
  });

  test("exposes interactive filter controls", async ({ page }) => {
    const filters = filterConsole(page);

    await expect(filters).toBeVisible();

    const select = filters.locator("select").first();
    if (await select.count()) {
      const options = select.locator("option");
      const optionCount = await options.count();
      test.skip(optionCount < 2, "The first filter select does not have an alternate option to exercise.");

      const currentValue = await select.inputValue();
      for (let index = 0; index < optionCount; index += 1) {
        const value = await options.nth(index).getAttribute("value");
        if (value && value !== currentValue) {
          await select.selectOption(value);
          await expect(select).toHaveValue(value);
          return;
        }
      }

      test.skip(true, "Could not find an alternate select value to apply.");
    }

    const textInput = filters.locator('input:not([type="hidden"])').first();
    test.skip(!(await textInput.count()), "No interactive filter input was found.");

    await textInput.fill("test");
    await expect(textInput).toHaveValue("test");
  });

  test("opens a task modal when task cards are present", async ({ page }) => {
    const cards = taskCards(page);
    test.skip(!(await cards.count()), "No task cards were present in the seeded environment.");

    await cards.first().click();
    await expect(taskModal(page)).toBeVisible();
  });
});
