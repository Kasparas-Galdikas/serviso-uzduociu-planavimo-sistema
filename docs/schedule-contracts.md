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
  - Must include `data-task-version="<uint>"` (Postgres `xmin` concurrency token).
  - Must include `data-duration-minutes="<int>"` (UI hint only; server recomputes duration).
  - Optional drag handle (daily view only):
    - `.task-drag-handle[draggable="true"]` inside `.task-card` for editable tasks.

### Day view drop slots

- `.schedule-slot`
  - Rendered only in **day** view, inside empty cells (no-overlap policy).
  - Must include:
    - `data-tech-id="<id>"`
    - `data-date="YYYY-MM-DD"`
    - `data-slot-start-minutes="<int>"`

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
    - `data-view`, `data-target-date`, `data-location`, `data-status`

## Backend contracts

### Task details

- `GET /api/tasks/{id}`
  - Returns JSON with at least:
    - `id`, `title`, `location`, `startAt`, `endAt`, `priority`, `status`, `isEditable`, `version`

### Save task

- `PATCH /api/tasks/{id}`
  - Payload JSON:
    - `{ title, location, startAt, endAt, priority, status }`
  - Status handling:
    - `409` or `403` → treated as non-editable / conflict (show message)
    - other non-2xx → error message

### Schedule tables

- `GET /Schedule/Table?{query}`
  - Returns HTML fragment rendered into `#scheduleTables`.

### Move task (drag & drop)

- `PATCH /api/tasks/{id}/move`
  - Payload JSON (intent only):
    - `{ targetTechnicianId, targetDate, targetSlotStartMinutes, expectedVersion }`
  - Server behavior:
    - Computes `StartAt/EndAt` from intent + persisted task duration.
    - Validates slot grid + working hours.
    - Forbids overlaps (`409`).
  - Intentionally unsupported:
    - Week view drag
    - Resize via drag
    - Multi-task drag
  - Status handling:
    - `409` ƒ+' stale or overlap (refresh table, show message)
    - `422` ƒ+' invalid time window (refresh table, show message)
    - `403` ƒ+' not editable (refresh table, show message)
