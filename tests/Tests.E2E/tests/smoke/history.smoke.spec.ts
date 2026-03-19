import { expect, test } from "@playwright/test";
import { openHistoryPanel } from "../../actions/history.actions";
import { authenticateAsManager } from "../../actions/auth.actions";
import { historyEmptyState, historyItems } from "../../fixtures/selectors";
import { today } from "../../support/date";

test.describe("history smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAsManager(page);
    await page.goto(`/Schedule?view=day&date=${today()}`);
  });

  test("history panel opens and shows either entries or the empty state", async ({ page }) => {
    await openHistoryPanel(page);

    const hasEntries = await historyItems(page).count() > 0;
    const showsEmpty = await historyEmptyState(page).isVisible().catch(() => false);
    expect(hasEntries || showsEmpty).toBe(true);
  });
});
