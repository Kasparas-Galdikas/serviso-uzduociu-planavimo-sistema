# Schedule JS contracts

This document defines the implicit contracts used by the Schedule feature JavaScript. The goal is to make DOM and backend dependencies explicit so refactors don’t silently break behavior.

## Event direction (important)

**Rule:** `schedule.state` is the single source of truth and **the only emitter** of Schedule events.

- `state → emits`
- `table/panel/tasks/filters → listen only` (they may call `state` public APIs to request changes; they must not write to `history` directly).

Events:

- `schedule:view:changed` — emitted when the effective view changes (`day`/`week`).
  - `detail`: `{ view, query }`
- `schedule:table:updated` — emitted after the schedule table HTML has been replaced.
  - `detail`: `{ view, query }`

## DOM contracts

### Required roots

- `#scheduleTables`
  - Replaced via AJAX when the table is refreshed.
  - Must exist on the schedule page.
  - Must expose datasets used as defaults:
    - `data-current-view` (`day|week`)
    - `data-first-free` (ISO date `YYYY-MM-DD`)
    - `data-active-end` (ISO date `YYYY-MM-DD`)

### Task cards

- `.task-card`
  - Clickable element that opens task details (daily view only).
  - Must include `data-task-id="<id>"`.

### Task detail panel (offcanvas)

- `#taskDetailModal`
  - Bootstrap Offcanvas.
  - Must contain form inputs by id used for population/saving:
    - `#taskDetailTitle`, `#taskDetailLocation`, `#taskDetailStart`, `#taskDetailEnd`,
      `#taskDetailPriority`, `#taskDetailStatus`
  - Optional controls used by panel UX:
    - `[data-panel-toggle]`, `[data-panel-toggle-icon]`, `[data-panel-resize]`

**Daily context rule:** task detail panel must never remain visible when `view !== day`.

### Filters roots

- `[data-schedule-filters="true"]`
  - Two roots exist:
    - inline filter bar (`data-schedule-filters-root="inline"`)
    - modal filter console (`data-schedule-filters-root="modal"`)
  - Required inputs:
    - `[data-filter="dayDate"]`, `[data-filter="weekDate"]`, `[data-filter="location"]`, `[data-filter="status"]`
  - View toggle buttons:
    - `[data-view-mode="day"]`, `[data-view-mode="week"]`
  - Apply button:
    - `[data-filter-apply]`
  - Optional edit mode controls:
    - `[data-edit-toggle]`, `[data-edit-action]`

### Navigation buttons

- `[data-nav]` buttons inside `#scheduleTables`
  - Supported data attributes:
    - `data-view`, `data-target-date`, `data-role`, `data-location`, `data-status`

## Backend contracts

### Task details

- `GET /tasks/{id}`
  - Returns JSON with at least:
    - `id`, `title`, `location`, `startAt`, `endAt`, `priority`, `status`, `isEditable`

### Save task

- `PATCH /tasks/{id}`
  - Payload JSON:
    - `{ title, location, startAt, endAt, priority, status }`
  - Status handling:
    - `409` or `403` → treated as non-editable / conflict (show message)
    - other non-2xx → error message

### Schedule tables

- `GET /Schedule/Table?{query}`
  - Returns HTML fragment rendered into `#scheduleTables`.

