using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Schedule;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    internal static class MockScheduleData
    {
        private static readonly Dictionary<int, string> Locations = new()
        {
            { 1, "Vilnius" }, { 2, "Kaunas" }, { 3, "Siauliai" }, { 4, "Klaipeda" }, { 5, "Panevezys" },
            { 6, "Vilnius" }, { 7, "Kaunas" }, { 8, "Vilnius" }, { 9, "Alytus" }, { 10, "Klaipeda" },
            { 11, "Vilnius" }, { 12, "Kaunas" }, { 13, "Siauliai" }, { 14, "Vilnius" }, { 15, "Kaunas" }
        };

        public static List<Technician> SeedTechnicians() => new()
        {
            new() { Id = 1, Name = "Austeja J.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h, savaites 40h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 2, Name = "Dovydas K.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h, savaites 45h", WeeklyHours = 22, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 3, Name = "Gabija P.", Location = "Siauliai", RulesSummary = "Dienos limitas 8h, savaites 42h", WeeklyHours = 15, WeeklyLimit = 42, DailyHours = 5, DailyLimit = 8, TravelSwitches = 3 },
            new() { Id = 4, Name = "Lukas V.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 19, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 5, Name = "Monika S.", Location = "Panevezys", RulesSummary = "Dienos limitas 8h", WeeklyHours = 12, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 0 },
            new() { Id = 6, Name = "Rokas M.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 21, WeeklyLimit = 35, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 7, Name = "Greta B.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 27, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 8, Name = "Mantas P.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 10, WeeklyLimit = 40, DailyHours = 3, DailyLimit = 8, TravelSwitches = 0 },
            new() { Id = 9, Name = "Ieva Z.", Location = "Alytus", RulesSummary = "Dienos limitas 8h", WeeklyHours = 8, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 10, Name = "Paulius T.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 24, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 11, Name = "Jurate R.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 16, WeeklyLimit = 40, DailyHours = 5, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 12, Name = "Tomas G.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 20, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 3 },
            new() { Id = 13, Name = "Elze K.", Location = "Siauliai", RulesSummary = "Dienos limitas 8h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 14, Name = "Vaidotas L.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 30, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 15, Name = "Karolina N.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 25, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 }
        };

        public static List<TaskItem> SeedTasksRange(DateTime startDate, int totalDays, bool markNewPlan)
        {
            var tasks = new List<TaskItem>();
            var id = 1;
            var durationOptions = new[] { 1.5, 2, 2.5, 3 };

            for (var dayOffset = 0; dayOffset < totalDays; dayOffset++)
            {
                var currentDate = startDate.Date.AddDays(dayOffset);
                var weekStart = WeekCalculator.GetWeekStart(currentDate);
                var weekShift = (GetStableSeed(weekStart) % 3) * 0.5;

                foreach (var techId in Locations.Keys)
                {
                    var start = 8.0 + weekShift;
                    var tasksToday = new List<TaskItem>();
                    var taskCount = 2 + ((techId + dayOffset + (int)(weekShift * 10)) % 3);

                    for (var i = 0; i < taskCount; i++)
                    {
                        var duration = durationOptions[(techId + dayOffset + i) % durationOptions.Length];
                        var end = start + duration;
                        if (end > 18) break;

                        var startDateTime = currentDate.AddHours(start);
                        var endDateTime = currentDate.AddHours(end);
                        tasksToday.Add(new TaskItem
                        {
                            Id = id++,
                            TechnicianId = techId,
                            Title = i == 0 ? "Ryto vizitas" : i == taskCount - 1 ? "Dienos pabaigos darbai" : "Darbai objektuose",
                            Location = Locations.TryGetValue(techId, out var loc) ? loc : "Vilnius",
                            Start = startDateTime,
                            End = endDateTime,
                            Priority = "vidutinis",
                            Status = "planuojama",
                            IsLoad = false,
                            IsConflict = false,
                            IsTravelSwitch = false,
                            IsNewPlan = markNewPlan,
                            Risk = "vidutinis"
                        });

                        start = end + 0.5;
                    }

                    if ((techId % 2 == 0 && currentDate.DayOfWeek == DayOfWeek.Friday) ||
                        (techId % 3 == 0 && currentDate.DayOfWeek == DayOfWeek.Tuesday))
                    {
                        var lastTask = tasksToday.LastOrDefault();
                        if (lastTask != null)
                        {
                            lastTask.End = lastTask.End.AddHours(1.5);
                        }
                    }

                    var totalHours = tasksToday.Sum(t => (t.End - t.Start).TotalHours);
                    var isConflict = totalHours > 8;
                    var isLoad = !isConflict && totalHours >= 6.5;

                    foreach (var t in tasksToday)
                    {
                        t.IsConflict = isConflict;
                        t.IsLoad = isLoad;
                        t.Priority = isConflict ? "aukstas" : isLoad ? "vidutinis" : "vidutinis";
                        t.Risk = isConflict ? "aukstas" : isLoad ? "vidutinis" : "zemas";
                    }

                    tasks.AddRange(tasksToday);
                }
            }

            return tasks;
        }

        private static int GetStableSeed(DateTime date)
        {
            var weekOfYear = ISOWeek.GetWeekOfYear(date);
            return unchecked((date.Year * 100) + weekOfYear);
        }
    }
}
