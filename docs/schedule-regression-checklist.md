# Schedule regression checklist (manual)

Run through these checks after changes to `wwwroot/js/schedule/**`.

## View + panel context

- Open a task in **day** view → task detail panel opens and push-layout applies on desktop widths.
- Switch to **week** view (nav or filter apply) while panel is open → panel closes and push-layout clears (no stale collapse/shrink UI).
- In **week** view, clicking a `.task-card` does **not** open the task detail panel.

## Navigation + URL

- Using `[data-nav]` buttons updates URL (pushState) and updates the table.
- Browser back/forward (popstate) updates the table to match URL.

## Filters

- Inline filter bar and modal filter console stay in sync with the URL after table changes.
- Switching filter view (day/week) updates only filter UI until "Taikyti" is pressed.

## Save flow

- In day view, edit a task and click save → table refreshes, panel closes, URL remains unchanged.

