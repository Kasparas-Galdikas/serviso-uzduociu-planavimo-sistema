import { expect, test } from "@playwright/test";
import { historyEmptyState, historyItems } from "../../fixtures/selectors";
import { openManagerHistoryForToday } from "../../flows/history.flows";

test.describe("history smoke", () => {
  test.beforeEach(async ({ page }) => {
    await openManagerHistoryForToday(page);
  });

  test("history panel opens and shows either entries or the empty state", async ({ page }) => {
    const hasEntries = await historyItems(page).count() > 0;
    const showsEmpty = await historyEmptyState(page).isVisible().catch(() => false);
    expect(hasEntries || showsEmpty).toBe(true);
  });
});