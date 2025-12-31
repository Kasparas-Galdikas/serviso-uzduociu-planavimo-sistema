using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public interface IScheduleLayoutBuilder
    {
        DailyTableLayout BuildDailyLayout(SchedulePageViewModel viewModel);
        WeeklyTableLayout BuildWeeklyLayout(SchedulePageViewModel viewModel);
    }

    public class ScheduleLayoutBuilder : IScheduleLayoutBuilder
    {
        public DailyTableLayout BuildDailyLayout(SchedulePageViewModel viewModel)
        {
            var day = viewModel.SelectedDate != default ? viewModel.SelectedDate : viewModel.WeekStart;

            var startMinutes = 8 * 60; // 08:00
            var endMinutes = 20 * 60; // 20:00
            var slotSize = 30; // 30 min

            var slots = Enumerable
                .Range(0, (endMinutes - startMinutes) / slotSize + 1)
                .Select(i => startMinutes + i * slotSize)
                .ToList();

            var columns = slots
                .Select(t => new TableColumn
                {
                    Title = TimeSpan.FromMinutes(t).ToString(@"hh\:mm")
                })
                .ToList();

            var rows = new List<TableRow>();

            foreach (var tech in viewModel.Technicians)
            {
                var tasks = viewModel.Tasks
                    .Where(t => t.TechnicianId == tech.Id && t.Start.Date == day.Date)
                    .OrderBy(t => t.Start)
                    .ToList();

                var techName = WebUtility.HtmlEncode(tech.Name);
                var techLocation = WebUtility.HtmlEncode(tech.Location);

                var cells = new List<TableCell>();
                var slotIndex = 0;

                while (slotIndex < slots.Count - 1)
                {
                    var slotStart = startMinutes + slotIndex * slotSize;
                    var task = tasks.FirstOrDefault(t => (int)t.Start.TimeOfDay.TotalMinutes == slotStart);

                    if (task != null)
                    {
                        var durationMinutes = (task.End - task.Start).TotalMinutes;
                        var durationSlots = (int)Math.Ceiling(durationMinutes / slotSize);
                        var remainingSlots = (slots.Count - 1) - slotIndex;
                        var colspan = Math.Max(1, Math.Min(durationSlots, remainingSlots));

                        var stateClass =
                            task.IsConflict ? "task-conflict" :
                            task.IsLoad ? "task-load" :
                            task.IsTravelSwitch ? "task-travel" :
                            "task-normal";

                        var contentHtml = $@"
<div class=""task-card {stateClass}"">
    <div class=""task-time"">{task.Start:HH:mm} - {task.End:HH:mm}</div>
    <div class=""task-title"">{WebUtility.HtmlEncode(task.Title)}</div>
    <div class=""task-location"">{WebUtility.HtmlEncode(task.Location)}</div>
</div>";

                        cells.Add(new TableCell
                        {
                            CssClass = "schedule-cell",
                            ColSpan = colspan,
                            ContentHtml = contentHtml
                        });

                        slotIndex += colspan;
                    }
                    else
                    {
                        cells.Add(new TableCell
                        {
                            CssClass = "schedule-cell empty-slot",
                            ContentHtml = string.Empty
                        });
                        slotIndex++;
                    }
                }

                cells.Add(new TableCell
                {
                    CssClass = "schedule-cell empty-slot",
                    ContentHtml = string.Empty
                });

                rows.Add(new TableRow
                {
                    HeaderHtml = $@"<div class=""fw-semibold"">{techName}</div><div class=""text-muted small"">{techLocation}</div>",
                    Cells = cells
                });
            }

            var headerLabel = $"Diena: {day:yyyy-MM-dd} ({day.ToString("dddd", CultureInfo.GetCultureInfo("lt-LT"))})";

            return new DailyTableLayout
            {
                HeaderLabel = headerLabel,
                IsToday = day.Date == DateTime.Today,
                Table = new TableShellModel
                {
                    FirstColumnTitle = "Technikai",
                    FirstColumnWidth = "180px",
                    TableCssClass = "schedule-table",
                    Columns = columns,
                    Rows = rows
                }
            };
        }

        public WeeklyTableLayout BuildWeeklyLayout(SchedulePageViewModel viewModel)
        {
            var lt = CultureInfo.GetCultureInfo("lt-LT");
            var days = Enumerable.Range(0, 7)
                .Select(i => viewModel.WeekStart.Date.AddDays(i))
                .ToList();

            string GetStateClass(double hours, int dailyLimit)
            {
                if (hours > dailyLimit) return "task-conflict";
                if (hours >= dailyLimit * 0.8) return "task-load";
                return "task-normal";
            }

            string GetDotClass(string state) => state switch
            {
                "task-conflict" => "is-conflict",
                "task-load" => "is-load",
                _ => "is-normal"
            };

            var columns = days
                .Select(d =>
                {
                    var label = lt.TextInfo.ToTitleCase(d.ToString("dddd yyyy-MM-dd", lt));
                    var isToday = d.Date == DateTime.Today;
                    return new TableColumn
                    {
                        Title = label,
                        CssClass = isToday ? "today-label" : null
                    };
                })
                .ToList();

            var rows = new List<TableRow>();

            foreach (var tech in viewModel.Technicians)
            {
                var cells = new List<TableCell>();

                foreach (var day in days)
                {
                    var dailyUrl = $"/Schedule/Index?view=day&date={day:yyyy-MM-dd}";
                    var tasks = viewModel.Tasks
                        .Where(t => t.TechnicianId == tech.Id && t.Start.Date == day.Date)
                        .ToList();

                    var totalHours = tasks.Sum(t => (t.End - t.Start).TotalHours);
                    var taskCount = tasks.Count;
                    var hasConflicts = tasks.Any(t => t.IsConflict);
                    var hasLoadFlags = tasks.Any(t => t.IsLoad);
                    var dailyLimit = tech.DailyLimit > 0 ? tech.DailyLimit : 8;
                    var freeHours = dailyLimit - totalHours;

                    var stateClass = hasConflicts
                        ? "task-conflict"
                        : hasLoadFlags
                            ? "task-load"
                            : GetStateClass(totalHours, dailyLimit);

                    var loadLine = $"{totalHours:0.#}h / {dailyLimit:0.#}h";
                    var taskLine = taskCount > 0 ? $"{taskCount} uzduotys" : "Nera uzduociu";
                    var freeLine = freeHours >= 0
                        ? $"+{freeHours:0.#}h laisva"
                        : $"{Math.Abs(freeHours):0.#}h truksta";
                    var freeClass = freeHours >= 0 ? "text-success" : "text-danger";
                    var dotTitle = stateClass switch
                    {
                        "task-conflict" => "Perkrova",
                        "task-load" => "Apkrova",
                        _ => "Normali"
                    };
                    var statusDot = $@"<span class=""state-dot-only {GetDotClass(stateClass)}"" title=""{dotTitle}"" aria-label=""{dotTitle}""></span>";

                    var content = $@"
<a href=""{dailyUrl}"" class=""text-decoration-none d-block"">
<div class=""task-card weekly-card p-2"">
    <div class=""d-flex justify-content-between align-items-start gap-2 mb-1"">
        <div class=""fact-line fw-semibold mb-0"">{loadLine}</div>
        {statusDot}
    </div>
    <div class=""d-flex justify-content-between align-items-start gap-2"">
        <div class=""weekly-facts"">
            <div class=""fact-line text-muted"">{WebUtility.HtmlEncode(taskLine)}</div>
            <div class=""fact-line {freeClass}"">{freeLine}</div>
        </div>
    </div>
</div>
</a>";

                    cells.Add(new TableCell
                    {
                        CssClass = "schedule-cell",
                        ColSpan = 1,
                        ContentHtml = content
                    });
                }

                rows.Add(new TableRow
                {
                    HeaderHtml = $@"<div class=""fw-semibold"">{WebUtility.HtmlEncode(tech.Name)}</div><div class=""text-muted small"">{WebUtility.HtmlEncode(tech.Location)}</div>",
                    Cells = cells
                });
            }

            var headerLabel = $"Savaite: {viewModel.WeekStart:yyyy-MM-dd} - {viewModel.WeekStart.AddDays(6):yyyy-MM-dd}";

            return new WeeklyTableLayout
            {
                HeaderLabel = headerLabel,
                Table = new TableShellModel
                {
                    FirstColumnTitle = "Technikai",
                    FirstColumnWidth = "180px",
                    TableCssClass = "schedule-table",
                    Columns = columns,
                    Rows = rows
                }
            };
        }
    }
}
