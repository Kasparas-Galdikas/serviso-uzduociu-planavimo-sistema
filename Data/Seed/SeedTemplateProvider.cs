using System;
using System.Collections.Generic;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    /// <summary>
    /// DEPRECATED: This class is no longer used.
    /// Draft plans now clone tasks from the active plan in the database instead of using mock templates.
    /// See PlanService.GenerateTasksFromActivePlanAsync for the new implementation.
    /// Kept for backward compatibility but returns empty list.
    /// </summary>
    [Obsolete("Use PlanService.GenerateTasksFromActivePlanAsync instead. This will be removed in a future version.")]
    public static class SeedTemplateProvider
    {
        private static readonly IReadOnlyList<TaskItem> TemplateWeek = new List<TaskItem>().AsReadOnly();

        /// <summary>
        /// Returns an empty template. Draft plans now use real database tasks.
        /// </summary>
        [Obsolete("Draft plans now clone from active plan. This will be removed in a future version.")]
        public static IReadOnlyList<TaskItem> WeekTemplate => TemplateWeek;
    }
}