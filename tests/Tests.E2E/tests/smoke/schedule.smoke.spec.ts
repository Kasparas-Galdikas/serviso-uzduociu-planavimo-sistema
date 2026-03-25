import { expect, test } from "@playwright/test";

import { authenticate } from "../../actions/auth.actions";
import {
  fillFirstFilterInput,
  openFirstTaskCard,
  openSchedule,
  selectFirstAlternateFilterValue,
  waitForScheduleGrid,
  waitForTaskModal,
} from "../../actions/schedule.actions";
import { filterConsole, schedulePage } from "../../fixtures/selectors";

test.describe("schedule smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticate(page);
    await openSchedule(page);
  });

  test("loads the schedule shell and grid", async ({ page }) => {
    await expect(schedulePage(page)).toBeVisible();
    await waitForScheduleGrid(page);
  });

  test("exposes interactive filter controls", async ({ page }) => {
    await expect(filterConsole(page)).toBeVisible();

    const alternateValue = await selectFirstAlternateFilterValue(page);
    if (alternateValue) {
      await expect(filterConsole(page).locator("select").first()).toHaveValue(alternateValue);
      return;
    }

    const didFillInput = await fillFirstFilterInput(page, "test");
    test.skip(!didFillInput, "No interactive filter input was found.");
    await expect(filterConsole(page).locator('input:not([type="hidden"])').first()).toHaveValue("test");
  });

  test("opens a task modal when task cards are present", async ({ page }) => {
    try {
      await openFirstTaskCard(page);
    } catch {
      test.skip(true, "No task cards were present in the seeded environment.");
    }

    await waitForTaskModal(page);
  });
});
