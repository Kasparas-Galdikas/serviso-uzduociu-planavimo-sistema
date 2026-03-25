import type { Locator, Page } from "@playwright/test";

declare global {
  interface Window {
    __codexScheduleActionCompletedAt?: number;
  }
}

async function prepareScheduleActionWait(page: Page): Promise<number> {
  return await page.evaluate(() => {
    const baseline = Date.now();
    window.__codexScheduleActionCompletedAt = 0;

    document.addEventListener(
      "schedule:action-completed",
      () => {
        window.__codexScheduleActionCompletedAt = Date.now();
      },
      { once: true }
    );

    return baseline;
  });
}

async function waitForScheduleActionCompleted(page: Page, baseline: number): Promise<void> {
  await page.waitForFunction((startedAt) => (window.__codexScheduleActionCompletedAt ?? 0) >= startedAt, baseline, {
    timeout: 15_000,
  });
}

export async function dragTaskHandleToTarget(page: Page, taskCard: Locator, target: Locator): Promise<void> {
  const baseline = await prepareScheduleActionWait(page);
  await taskCard.locator(".task-drag-handle").dragTo(target);
  await waitForScheduleActionCompleted(page, baseline);
}

export async function resizeTaskRightHandleToSlot(page: Page, rightHandle: Locator, targetSlot: Locator): Promise<void> {
  const handleBox = await rightHandle.boundingBox();
  if (!handleBox) {
    throw new Error("Resize right handle not found in DOM.");
  }

  const targetBox = await targetSlot.boundingBox();
  if (!targetBox) {
    throw new Error("Resize target slot not found in DOM.");
  }

  const baseline = await prepareScheduleActionWait(page);

  await page.mouse.move(handleBox.x + handleBox.width / 2, handleBox.y + handleBox.height / 2);
  await page.mouse.down();
  await page.mouse.move(targetBox.x + targetBox.width / 2, targetBox.y + targetBox.height / 2, { steps: 15 });
  await page.mouse.up();

  await waitForScheduleActionCompleted(page, baseline);
}
