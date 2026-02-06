using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.Scheduling
{
    public interface IScheduleLayoutBuilder
    {
        DailyTableLayout BuildDailyLayout(SchedulePageViewModel viewModel);
        WeeklyTableLayout BuildWeeklyLayout(SchedulePageViewModel viewModel);
    }

    public class ScheduleLayoutBuilder : IScheduleLayoutBuilder
    {
        private readonly IScheduleTimeService _timeService;

        public ScheduleLayoutBuilder(IScheduleTimeService timeService)
        {
            _timeService = timeService;
        }

        public DailyTableLayout BuildDailyLayout(SchedulePageViewModel viewModel)
        {
            var day = viewModel.SelectedDate != default ? viewModel.SelectedDate : viewModel.WeekStart;

            var startMinutes = ScheduleGrid.DayStartMinutes;
            var endMinutes = ScheduleGrid.DayEndMinutes;
            var slotSize = ScheduleGrid.SlotSizeMinutes;

            // Columns represent time intervals, including both start and end time slots
            var slotCount = (endMinutes - startMinutes) / slotSize + 1;
            var slots = Enumerable
                .Range(0, slotCount)
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

                var cells = new List<TableCell>();
                var slotIndex = 0;

                while (slotIndex < slots.Count)
                {
                    var slotStart = startMinutes + slotIndex * slotSize;
                    var slotTasks = tasks
                        .Where(t => (int)t.Start.TimeOfDay.TotalMinutes == slotStart)
                        .OrderBy(t => t.Source == TaskPlanSource.Active ? 0 : 1)
                        .ToList();

                    if (slotTasks.Any())
                    {
                        var durationMinutes = slotTasks.Max(t => (t.End - t.Start).TotalMinutes);
                        var durationSlots = (int)Math.Ceiling(durationMinutes / slotSize);
                        var remainingSlots = slots.Count - slotIndex;
                        var colspan = Math.Max(1, Math.Min(durationSlots, remainingSlots));

                        cells.Add(new TableCell
                        {
                            CssClass = "schedule-cell",
                            ColSpan = colspan,
                            DailyData = new DailyCellData
                            {
                                Tasks = slotTasks
                            }
                        });

                        slotIndex += colspan;
                    }
                    else
                    {
                        var emptySlotStartMinutes = startMinutes + slotIndex * slotSize;
                        var timeLabel = TimeSpan.FromMinutes(emptySlotStartMinutes).ToString(@"hh\:mm");

                        cells.Add(new TableCell
                        {
                            CssClass = "schedule-cell empty-slot",
                            DailyData = new DailyCellData
                            {
                                EmptySlot = new EmptySlotInfo
                                {
                                    Date = day.ToString("yyyy-MM-dd"),
                                    TechnicianId = tech.Id,
                                    SlotStartMinutes = emptySlotStartMinutes,
                                    TimeLabel = timeLabel
                                }
                            }
                        });
                        slotIndex++;
                    }
                }

                rows.Add(new TableRow
                {
                    TechnicianInfo = new TechnicianHeaderInfo
                    {
                        TechnicianId = tech.Id,
                        Name = tech.Name,
                        Location = tech.Location
                    },
                    Cells = cells
                });
            }

            var headerLabel = $"Diena: {day:yyyy-MM-dd} ({day.ToString("dddd", CultureInfo.GetCultureInfo("lt-LT"))})";

            return new DailyTableLayout
            {
                HeaderLabel = headerLabel,
                IsToday = day.Date == _timeService.GetLocalToday(),
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

            var columns = days
                .Select(d =>
                {
                    var label = lt.TextInfo.ToTitleCase(d.ToString("dddd yyyy-MM-dd", lt));
                    var isToday = d.Date == _timeService.GetLocalToday();
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
                    var dailyUrl = $"/Schedule/Index?view=day&date={day:yyyy-MM-dd}&technicianId={tech.Id}";
                    var tasks = viewModel.Tasks
                        .Where(t => t.TechnicianId == tech.Id && t.Start.Date == day.Date)
                        .ToList();

                    var totalHours = tasks.Sum(t => (t.End - t.Start).TotalHours);
                    var taskCount = tasks.Count;
                    var hasConflicts = tasks.Any(t => t.IsConflict);
                    var hasLoadFlags = tasks.Any(t => t.IsLoad || t.OverloadHours > 0);
                    var dailyLimit = tech.DailyLimit > 0 ? tech.DailyLimit : 8;
                    var freeHours = dailyLimit - totalHours;

                    var stateClass = hasConflicts
                        ? "task-conflict"
                        : hasLoadFlags
                            ? "task-load"
                            : "task-normal";

                    cells.Add(new TableCell
                    {
                        CssClass = "schedule-cell",
                        ColSpan = 1,
                        WeeklyData = new WeeklyCellData
                        {
                            DailyUrl = dailyUrl,
                            TotalHours = totalHours,
                            TaskCount = taskCount,
                            DailyLimit = dailyLimit,
                            FreeHours = freeHours,
                            HasConflicts = hasConflicts,
                            HasLoadFlags = hasLoadFlags,
                            StateClass = stateClass
                        }
                    });
                }

                rows.Add(new TableRow
                {
                    TechnicianInfo = new TechnicianHeaderInfo
                    {
                        TechnicianId = tech.Id,
                        Name = tech.Name,
                        Location = tech.Location
                    },
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
