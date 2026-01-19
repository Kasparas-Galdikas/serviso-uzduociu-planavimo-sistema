using System;
using System.Linq;
using FluentAssertions;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using Xunit;

namespace Tests.Unit.Scheduling;

public class ScheduleLayoutBuilderTests
{
    [Fact]
    public void DailyLayout_ProducesExpectedSlotsAndRows()
    {
        var builder = new ScheduleLayoutBuilder();
        var vm = new SchedulePageViewModel
        {
            SelectedDate = new DateTime(2026, 1, 20),
            WeekStart = new DateTime(2026, 1, 19),
            Technicians = new()
            {
                new Technician { Id = 1, Name = "Tech 1", Location = "North" },
                new Technician { Id = 2, Name = "Tech 2", Location = "South" }
            }
        };

        var layout = builder.BuildDailyLayout(vm);

        var columns = layout.Table.Columns.ToList();
        columns.Should().HaveCount((ScheduleGrid.DayEndMinutes - ScheduleGrid.DayStartMinutes) / ScheduleGrid.SlotSizeMinutes + 1);
        layout.Table.Rows.Should().HaveCount(2);
        layout.Table.Rows.All(r => r.Cells.Count() == columns.Count).Should().BeTrue();
    }

    [Fact]
    public void WeeklyLayout_Marks_Conflict_State()
    {
        var builder = new ScheduleLayoutBuilder();
        var weekStart = new DateTime(2026, 1, 19);
        var vm = new SchedulePageViewModel
        {
            WeekStart = weekStart,
            SelectedDate = weekStart,
            Technicians = new() { new Technician { Id = 1, Name = "Tech 1", Location = "North", DailyLimit = 8, WeeklyLimit = 40 } },
            Tasks = new()
            {
                new TaskItem
                {
                    Id = 1,
                    TechnicianId = 1,
                    Start = weekStart.AddHours(9),
                    End = weekStart.AddHours(10),
                    IsConflict = true
                }
            }
        };

        var layout = builder.BuildWeeklyLayout(vm);
        var row = layout.Table.Rows.Single();
        var firstCell = row.Cells.First();

        firstCell.ContentHtml.Should().Contain("Perkrova");
    }
}
