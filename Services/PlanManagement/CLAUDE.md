# PlanManagement — Architecture Contract

Read this before making any change to `PlanService.cs` or any file under `Orchestrator/`.

---

## What this layer is

`PlanService` is the **single public entry point** for all plan and task operations.
Everything outside this folder (controllers, background jobs, other services) calls
`IPlanService` only. The concrete class `PlanService` delegates every call to an
internal orchestrator. No business logic lives in `PlanService.cs` itself.

---

## File map

```
Services/PlanManagement/
├── PlanService.cs                       ← thin facade, delegation only
├── Interfaces/IPlanService.cs           ← public contract, never break this
│
└── Orchestrator/
    ├── PlanService.PlanLifecycle.cs     ← plan CRUD, publish, discard
    ├── PlanService.TaskQuery.cs         ← read-only queries, zero writes
    ├── PlanService.TaskCreation.cs      ← single + bulk task creation
    ├── PlanService.TaskModification.cs  ← update, move, swap, reschedule
    ├── PlanService.TaskDeletion.cs      ← delete + optional compaction
    ├── PlanService.TaskPreview.cs       ← what-if simulations, zero writes
    └── Shared/
        ├── SchedulePlacementValidator.cs  ← all timing/travel/overlap checks
        ├── ServiceLocationResolver.cs     ← address + service object lookup
        └── RouteEvaluationCaller.cs       ← safe route recalculation triggers
```

---

## Who calls what

### External call flows

| Caller action | Entry point | Orchestrator used | Shared helpers used |
|---|---|---|---|
| Create a task | `CreateTaskAsync` | `TaskCreation` | `PlacementValidator`, `LocationResolver`, `RouteEvaluationCaller`, `PlanLifecycle` |
| Bulk create | `BulkCreateTasksAsync` | `TaskCreation` | same as above |
| Move a task | `MoveTaskAsync` | `TaskModification` | `PlacementValidator`, `RouteEvaluationCaller`, `PlanLifecycle` |
| Swap two tasks | `SwapTasksSmartAsync` | `TaskModification` | `PlacementValidator`, `RouteEvaluationCaller`, `PlanLifecycle` |
| Update task fields | `UpdateTaskAsync` | `TaskModification` | `PlacementValidator`, `LocationResolver`, `RouteEvaluationCaller`, `PlanLifecycle` |
| Reschedule time | `UpdateTaskTimeAsync` | `TaskModification` | `PlacementValidator`, `RouteEvaluationCaller` |
| Delete a task | `DeleteTaskAsync` | `TaskDeletion` | `RouteEvaluationCaller`, `PlanLifecycle` |
| Technician marks done | `TechnicianUpdateTaskAsync` | `TaskModification` | — |
| Drag preview | `PreviewDragAsync` | `TaskPreview` | `PlacementValidator` |
| Swap preview | `PreviewSwapSmartAsync` | `TaskPreview` | `PlacementValidator` |
| Workload snapshot | `GetWorkloadSnapshotAsync` | `TaskQuery` | — |
| Task detail | `GetTaskDetailAsync` | `TaskQuery` | — |
| Slot suggestion | `GetSuggestedNextSlotAsync` | `TaskQuery` | — |
| Publish draft | `PublishDraftAsync` | `PlanLifecycle` | — |
| Create draft | `CreateDraftFromActiveAsync` | `PlanLifecycle` | — |

### Internal call flows (orchestrator → orchestrator)

`TaskCreation`, `TaskModification`, `TaskDeletion` all call into `PlanLifecycle` for:
- `GetOrExtendActivePlanAsync` — resolve or create the target plan for a task date
- `AdjustPlanSpanAsync` — shrink/grow the plan's date boundaries after a change

These two methods are `internal` on `PlanServicePlanLifecycle`. No other cross-orchestrator calls exist.

---

## Shared helper responsibilities

### `SchedulePlacementValidator`
Answers: *can a task legally sit at this time slot?*

Validation order:
1. Date not in the past
2. Start/end minutes are valid and grid-aligned
3. Slot fits within the technician's working window
4. Technician exists
5. User has permission (via `ITaskPermissionService`)
6. No overlap with existing tasks
7. Travel from prior task is feasible (via `ITaskTravelFeasibilityService`)
8. If travel fails — attempt repair plan via `IScheduleRepairService`

Also owns the `MapPlacementErrorFor*` static methods that translate validation
failures into typed result objects for each operation.

> Add new placement rules here. Never duplicate validation logic in an orchestrator.

### `ServiceLocationResolver`
Answers: *what is the DB id for this service location?*

- `FindOrCreateServiceObjectAsync` — upserts address + object rows, returns object id
- `TryLookupServiceAddressIdAsync` — read-only, used during bulk dry-run validation

> Geocoding, address normalization, and deduplication logic belongs here.

### `RouteEvaluationCaller`
Triggers route recalculation after schedule changes. Both methods swallow exceptions
so a route engine failure never fails the main request.

- `TriggerRouteEvaluationSafelyAsync(technicianId, day, action)` — full day recalc
- `TriggerTaskRouteImpactSafelyAsync(taskId, action)` — single task impact

> No orchestrator imports `IRouteEvaluationService` directly. Always go through this class.

---

## Mutation operation pattern

Every operation that writes to the database follows this sequence:

```
1. Load entity + check it exists
2. Check optimistic concurrency version (xmin)
3. Check permissions
4. Validate placement (via SchedulePlacementValidator)
5. Open transaction
6. Apply changes + SaveChanges
7. Trigger route recalculation (via RouteEvaluationCaller)
8. Post-save travel validation — if fails, attempt repair or rollback
9. Commit (or rollback)
10. Adjust plan span (via PlanLifecycle.AdjustPlanSpanAsync)
11. Recalculate evaluations (via IEvaluationEngine)
12. Record history (via ITaskHistoryWriter)
```

Not every step applies to every operation, but **never skip step 2, 3, or 5 for
any mutation**.

---

## Read-only operation pattern

All queries live in `TaskQuery`. They:
- Use `AsNoTracking()` on every EF query
- Never call `SaveChanges`
- Never call `RouteEvaluationCaller`
- Never call `IEvaluationEngine`

> If you find yourself writing a DB read in an orchestrator that isn't `TaskQuery`,
> ask whether it belongs there instead.

---

## Preview operation pattern

All simulations live in `TaskPreview`. They:
- Run full placement validation (via `SchedulePlacementValidator`) so results are accurate
- Never call `SaveChanges` or open transactions
- Never call `RouteEvaluationCaller`
- Never record history
- Return a result describing what *would* happen

---

## When to add to an existing orchestrator vs create a new one

### Add to an existing orchestrator when:
- The new method fits naturally in one of the six existing concerns
- The orchestrator stays under ~900 lines after the addition
- The method uses the same or a strict subset of the orchestrator's existing dependencies
- Examples: new task creation variant → `TaskCreation`; new report query → `TaskQuery`

### Create a new orchestrator (`PlanService.NewConcern.cs`) when:
- The new behavior does not belong to any existing concern without forcing it
- Adding it would push an existing orchestrator well past 900 lines
- The new behavior requires a materially different set of dependencies
- The new behavior could be developed, tested, or deployed independently
- Examples: `PlanService.Notifications.cs` for push events; `PlanService.Recurrence.cs` for recurring task generation

**How to add a new orchestrator:**
1. Create `Orchestrator/PlanService.NewConcern.cs` with an `internal class PlanServiceNewConcern`
2. Add a field `private readonly PlanServiceNewConcern _newConcern;` to `PlanService.cs`
3. Construct it in `PlanService`'s 11-arg constructor
4. Add the delegation method(s) to `PlanService.cs`
5. Add new method(s) to `IPlanService` if they need to be public

---

## Invariants — never break these

| Rule | Reason |
|---|---|
| All public surface goes through `IPlanService` | Controllers and tests depend on the interface, not the concrete class |
| Orchestrators are `internal` | They are implementation details; nothing outside `PlanManagement` should reference them |
| Shared helpers are `internal` | Same reason — they are not part of the public contract |
| `TaskQuery` and `TaskPreview` never write | Keeps reads safe to call in parallel and easy to reason about |
| `RouteEvaluationCaller` never throws | Route failures must not roll back schedule changes |
| Optimistic concurrency (xmin) checked on every mutation | Prevents silent overwrites from concurrent users |
| History recorded on every mutation | Audit trail is non-negotiable |
