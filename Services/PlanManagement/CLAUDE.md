# PlanManagement - Architecture Contract

Read this before changing `PlanService.cs` or any file under `Orchestrator/`.

---

## What this layer is

`PlanService` is the single public entry point for plan and task operations.
Controllers, background jobs, and other services should depend on `IPlanService`,
not on internal orchestrators or helpers.

`PlanService.cs` is a facade. Its job is:
- constructor wiring
- dependency composition
- method delegation

Business behavior should live in orchestrators and shared helpers, not in the facade.

---

## Current file map

```text
Services/PlanManagement/
|-- PlanService.cs
|-- Interfaces/IPlanService.cs
`-- Orchestrator/
    |-- PlanService.PlanLifecycle.cs
    |-- PlanService.TaskQuery.cs
    |-- PlanService.TaskCreation.cs
    |-- PlanService.TaskModification.cs
    |-- PlanService.TaskDeletion.cs
    |-- PlanService.TaskPreview.cs
    |-- PlanService.AssignmentMatching.cs
    |-- PlanService.TechnicianUnavailability.cs
    `-- Shared/
        |-- RouteEvaluationCaller.cs
        |-- RoutePreviewLookupBuilder.cs
        |-- PlanScopeResolver.cs
        |-- PlanScopeSegregationService.cs
        |-- SchedulePlacementValidator.cs
        |-- ServiceLocationResolver.cs
        `-- TechnicianUnavailabilityImpactResolver.cs
```

Primary concern ownership:
- `PlanLifecycle`: draft/active/superseded plan lifecycle and plan span adjustments
- `TaskQuery`: read-only task and workload queries
- `TaskCreation`: single-task and bulk-task creation flows
- `TaskModification`: update, move, resize, swap, technician status updates
- `TaskDeletion`: delete and bulk delete flows
- `TaskPreview`: read-only simulations and previews
- `AssignmentMatching`: auto-assignment suggestion and chained bulk matching
- `TechnicianUnavailability`: create/update/delete unavailability and execute displacement/restoration

---

## Public surface to orchestrator map

| Public method | Owner |
|---|---|
| `GetActivePlanAsync` | `PlanLifecycle` |
| `GetDraftPlanAsync` | `PlanLifecycle` |
| `PublishDraftAsync` | `PlanLifecycle` |
| `DiscardDraftAsync` | `PlanLifecycle` |
| `RecalculateCurrentPlansAsync` | `PlanLifecycle` |
| `GetWorkloadSnapshotAsync` | `TaskQuery` |
| `GetTaskDetailAsync` | `TaskQuery` |
| `GetTaskVersionAsync` | `TaskQuery` |
| `TechnicianExistsAsync` | `TaskQuery` |
| `HasTaskOverlapAsync` | `TaskQuery` |
| `GetSuggestedNextSlotAsync` | `TaskQuery` |
| `CreateTaskAsync` | `TaskCreation` |
| `CreateDraftTaskAsync` | `TaskCreation` |
| `BulkValidateTasksAsync` | `TaskCreation` |
| `BulkCreateTasksAsync` | `TaskCreation` |
| `UpdateTaskAsync` | `TaskModification` |
| `UpdateTaskTimeAsync` | `TaskModification` |
| `MoveTaskAsync` | `TaskModification` |
| `SwapTasksAsync` | `TaskModification` |
| `SwapTasksSmartAsync` | `TaskModification` |
| `TechnicianUpdateTaskAsync` | `TaskModification` |
| `DeleteTaskAsync` | `TaskDeletion` |
| `BulkDeleteTasksAsync` | `TaskDeletion` |
| `PreviewDragAsync` | `TaskPreview` |
| `PreviewSwapSmartAsync` | `TaskPreview` |
| `PreviewDisplacementAsync` | `TaskPreview` |
| `SuggestAutoAssignmentAsync` | `AssignmentMatching` |
| `SuggestBulkAutoAssignmentsAsync` | `AssignmentMatching` |
| `ApplyAutomaticAssignmentsAsync` | `AssignmentMatching` |
| `DisplaceTasksToBacklogAsync` | `TechnicianUnavailability` |
| `UpdateUnavailabilityAsync` | `TechnicianUnavailability` |
| `DeleteUnavailabilityAsync` | `TechnicianUnavailability` |

---

## Shared helper responsibilities

### `SchedulePlacementValidator`
Answers: can a task legally sit in this slot?

Owns:
- time-range validation
- grid alignment validation
- work-window validation
- overlap checks
- permission-aware placement checks
- travel feasibility checks
- repair-plan entry points
- placement-error mapping helpers

Rule:
- new placement rules belong here
- do not duplicate placement logic in multiple orchestrators

### `ServiceLocationResolver`
Answers: what address/service object should this operation use?

Owns:
- address lookup
- address creation
- service object lookup
- service object upsert

Rule:
- address normalization and deduplication belong here

### `RouteEvaluationCaller`
Answers: how do write flows trigger route recalculation safely?

Owns:
- full-day route recalculation trigger
- task-specific route-impact trigger
- exception swallowing/logging so route failures do not fail core writes

Rule:
- write-side route recalculation should go through this helper

### `RoutePreviewLookupBuilder`
Answers: how do preview flows obtain route lookups without embedding route-service
calls inside preview orchestrators?

Owns:
- preview-time pairwise route hydration
- preview-time travel lookup construction
- failure isolation so preview can fall back to cached route data

Rule:
- preview orchestrators should use this helper rather than injecting `IRouteEvaluationService` directly

### `TechnicianUnavailabilityImpactResolver`
Answers: which tasks are affected by a technician unavailability window?

Owns:
- affected-task discovery
- normalized impact windows used by preview and mutation flows

Rule:
- unavailability impact scans belong here, not duplicated in orchestrators

### `PlanScopeResolver`
Answers: which plan scope/date scope applies to a request?

Owns:
- read-only scope resolution used by placement and assignment flows

Rule:
- scope rules belong here

---

## Operation patterns

### Mutation flows

Typical write sequence:

```text
1. Load entity or target scope
2. Check existence
3. Check permissions
4. Check optimistic concurrency when the operation is versioned
5. Validate placement/business rules
6. Open transaction when multiple stateful writes are involved
7. Persist changes
8. Trigger route recalculation safely if applicable
9. Recalculate evaluations if applicable
10. Record history
11. Commit
12. Run post-write span/scope cleanup if applicable
```

Notes:
- not every mutation uses every step
- create flows do not have an existing row version to compare before insert
- updates, deletes, moves, swaps, and technician state changes should preserve concurrency protection where exposed by the contract

### Read-only flows

Read-only queries should:
- use `AsNoTracking()`
- never call `SaveChanges`
- never open write transactions
- never record history

### Preview flows

Preview flows should:
- remain zero-write
- use real validation logic so results stay accurate
- avoid history writes
- avoid write-side recalculation triggers

---

## Invariants

| Rule | Reason |
|---|---|
| All public operations go through `IPlanService` | Keeps the external contract stable |
| Orchestrators remain `internal` | They are implementation details |
| Shared helpers remain `internal` | They are implementation details |
| `PlanService.cs` stays a facade | Keeps wiring separate from business logic |
| `TaskQuery` and preview methods stay zero-write | Makes them safe and predictable |
| `RouteEvaluationCaller` must not throw into write flows | Route failures must not roll back core changes |
| Versioned mutations must enforce optimistic concurrency | Prevents silent overwrites |
| User-visible mutations must record history | Audit trail is required |
