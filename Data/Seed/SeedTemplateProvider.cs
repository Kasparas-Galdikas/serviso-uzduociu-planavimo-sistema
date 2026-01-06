using System;
using System.Collections.Generic;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    /// <summary>
    /// Provides a reusable mock task template that can be mapped into any draft window.
    /// Keeps template data decoupled from plan generation logic.
    /// </summary>
    public static class SeedTemplateProvider
    {
        private static readonly DateTime TemplateAnchor = new DateTime(2026, 01, 05); // Monday
        // Full 7-day template (includes weekend data if present in mocks). Reused for every draft.
        private static readonly IReadOnlyList<TaskItem> TemplateWeek =
            MockScheduleData.SeedTasksRange(TemplateAnchor, 7, false).AsReadOnly();

        /// <summary>
        /// Gets the base template tasks (immutable) for a full week.
        /// Consumers can choose to skip weekends when mapping into a plan.
        /// </summary>
        public static IReadOnlyList<TaskItem> WeekTemplate => TemplateWeek;
    }
}
