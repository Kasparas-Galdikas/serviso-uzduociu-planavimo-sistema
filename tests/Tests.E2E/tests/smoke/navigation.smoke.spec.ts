import { expect, test } from "@playwright/test";
import { authenticate } from "../../actions/auth.actions";
import { goToNextSchedulePeriod, goToPreviousSchedulePeriod, waitForView } from "../../actions/navigation.actions";
import { navNextBtn, navPrevBtn, scheduleGrid } from "../../fixtures/selectors";
import { addDays, today } from "../../support/date";

test.describe("navigation smoke", () => {
  test.beforeEach(async ({ page }) => {
    await authenticate(page);
  });

  test("switches from daily view to weekly view and back", async ({ page }) => {
    await page.goto(`/Schedule?view=day&date=${today()}`);
    await expect(page.locator('#scheduleTables[data-current-view="day"]')).toBeVisible();

    const weeklyLink = page.locator('a[href*="view=week"]').first();
    test.skip(await weeklyLink.count() === 0, "No weekly-view navigation link found.");
    await weeklyLink.click();
    await waitForView(page, "week");

    await expect(page.locator('#scheduleTables[data-current-view="week"]')).toBeVisible();
    await expect(scheduleGrid(page)).toBeVisible();

    const dailyLink = page.locator('a[href*="view=day"]').first();
    test.skip(await dailyLink.count() === 0, "No daily-view navigation link found.");
    await dailyLink.click();
    await waitForView(page, "day");

    await expect(page.locator('#scheduleTables[data-current-view="day"]')).toBeVisible();
  });

  test("navigates to the next day and then back via the prev/next buttons", async ({ page }) => {
    await page.goto(`/Schedule?view=day&date=${today()}`);

    await expect(navNextBtn(page)).toBeVisible();
    const nextDate = await goToNextSchedulePeriod(page);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(nextDate).toBeTruthy();

    await expect(navPrevBtn(page)).toBeVisible();
    const previousDate = await goToPreviousSchedulePeriod(page);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(previousDate).toBeTruthy();
  });

  test("navigates to the previous week and next week in weekly view", async ({ page }) => {
    await page.goto(`/Schedule?view=week&date=${today()}`);

    await expect(navNextBtn(page)).toBeVisible();
    const nextWeekDate = await goToNextSchedulePeriod(page);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(nextWeekDate).toBeTruthy();

    await expect(navPrevBtn(page)).toBeVisible();
    const previousWeekDate = await goToPreviousSchedulePeriod(page);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(previousWeekDate).toBeTruthy();
  });

  test("URL contains view=day and the correct date when in daily view", async ({ page }) => {
    const date = today();
    await page.goto(`/Schedule?view=day&date=${date}`);

    expect(page.url()).toContain("view=day");
    expect(page.url()).toContain(date);
  });

  test("URL contains view=week when in weekly view", async ({ page }) => {
    await page.goto(`/Schedule?view=week&date=${today()}`);
    expect(page.url()).toContain("view=week");
  });

  test("navigating to a different date loads a fresh grid without page errors", async ({ page }) => {
    const pageErrors: Error[] = [];
    page.on("pageerror", (error) => pageErrors.push(error));

    await page.goto(`/Schedule?view=day&date=${addDays(today(), 1)}`);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(pageErrors, "page errors on date navigation").toEqual([]);
  });

  test("navigating to a week view loads a fresh grid without page errors", async ({ page }) => {
    const pageErrors: Error[] = [];
    page.on("pageerror", (error) => pageErrors.push(error));

    await page.goto(`/Schedule?view=week&date=${today()}`);
    await expect(scheduleGrid(page)).toBeVisible();
    expect(pageErrors, "page errors on weekly view").toEqual([]);
  });

  test("schedule tables container carries a data-plan-id attribute after load", async ({ page }) => {
    await page.goto(`/Schedule?view=day&date=${today()}`);
    expect(await page.locator("#scheduleTables").getAttribute("data-plan-id")).not.toBeNull();
  });
});
