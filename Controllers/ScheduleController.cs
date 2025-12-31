using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class ScheduleController : Controller
    {
        [HttpGet]
        public IActionResult Index(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var vm = BuildViewModel(view, date, location, status);
            return View(vm);
        }

        [HttpGet]
        public IActionResult Table(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var vm = BuildViewModel(view, date, location, status);
            var partial = string.Equals(vm.ViewMode, "week", StringComparison.OrdinalIgnoreCase)
                ? "~/Views/Schedule/Partials/Tables/_WeeklyTable.cshtml"
                : "~/Views/Schedule/Partials/Tables/_DailyTable.cshtml";

            return PartialView(partial, vm);
        }

        private SchedulePageViewModel BuildViewModel(string view, string? date, string? location, string? status)
        {
            var selectedDate = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
            {
                selectedDate = parsedDate.Date;
            }

            var weekStart = selectedDate.AddDays(-(int)selectedDate.DayOfWeek + (int)DayOfWeek.Monday);
            var viewMode = string.Equals(view, "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day";
            var day = new Func<int, DateTime>(offset => weekStart.AddDays(offset));

            var technicians = new List<Technician>
            {
                new() { Id = 1, Name = "Austeja J.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h, savaites 40h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
                new() { Id = 2, Name = "Dovydas K.", Location = "Kaunas", RulesSummary = "Dienos limitas 10h, savaites 45h", WeeklyHours = 22, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 10, TravelSwitches = 1 },
                new() { Id = 3, Name = "Gabija P.", Location = "Siauliai", RulesSummary = "Dienos limitas 9h, savaites 42h", WeeklyHours = 15, WeeklyLimit = 42, DailyHours = 5, DailyLimit = 9, TravelSwitches = 3 },
                new() { Id = 4, Name = "Lukas V.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 19, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
                new() { Id = 5, Name = "Monika S.", Location = "Panevezys", RulesSummary = "Dienos limitas 8h", WeeklyHours = 12, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 0 },
                new() { Id = 6, Name = "Rokas M.", Location = "Vilnius", RulesSummary = "Dienos limitas 7h", WeeklyHours = 21, WeeklyLimit = 35, DailyHours = 6, DailyLimit = 7, TravelSwitches = 2 },
                new() { Id = 7, Name = "Greta B.", Location = "Kaunas", RulesSummary = "Dienos limitas 9h", WeeklyHours = 27, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 9, TravelSwitches = 1 },
                new() { Id = 8, Name = "Mantas P.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 10, WeeklyLimit = 40, DailyHours = 3, DailyLimit = 8, TravelSwitches = 0 },
                new() { Id = 9, Name = "Ieva Z.", Location = "Alytus", RulesSummary = "Dienos limitas 8h", WeeklyHours = 8, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 1 },
                new() { Id = 10, Name = "Paulius T.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 24, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
                new() { Id = 11, Name = "Jurate R.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 16, WeeklyLimit = 40, DailyHours = 5, DailyLimit = 8, TravelSwitches = 1 },
                new() { Id = 12, Name = "Tomas G.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 20, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 3 },
                new() { Id = 13, Name = "Elze K.", Location = "Siauliai", RulesSummary = "Dienos limitas 8h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
                new() { Id = 14, Name = "Vaidotas L.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 30, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
                new() { Id = 15, Name = "Karolina N.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 25, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 }
            };

            var tasks = new List<TaskItem>
            {
                // Day 0 (Mon)
                new() { Id = 1, TechnicianId = 1, Title = "Kabeliu patikra", Location = "Vilnius", Start = day(0).AddHours(9), End = day(0).AddHours(11), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                new() { Id = 2, TechnicianId = 2, Title = "Wifi auditavimas", Location = "Panevezys", Start = day(0).AddHours(12), End = day(0).AddHours(16), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = true, Risk = "aukstas" },
                new() { Id = 3, TechnicianId = 3, Title = "Sviesolaidzio matavimai", Location = "Siauliai", Start = day(0).AddHours(8), End = day(0).AddHours(11), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "vidutinis" },
                new() { Id = 4, TechnicianId = 4, Title = "Klaipedos apsilankymas", Location = "Klaipeda", Start = day(0).AddHours(10), End = day(0).AddHours(18), Priority = "vidutinis", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = false, Risk = "aukstas" },
                new() { Id = 5, TechnicianId = 5, Title = "Panevezio remontas", Location = "Panevezys", Start = day(0).AddHours(9), End = day(0).AddHours(12), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                // Day 1 (Tue)
                new() { Id = 6, TechnicianId = 1, Title = "Serverio diagnostika", Location = "Vilnius", Start = day(1).AddHours(12), End = day(1).AddHours(15), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = false, IsTravelSwitch = false, Risk = "vidutinis" },
                new() { Id = 7, TechnicianId = 6, Title = "UPS testas", Location = "Vilnius", Start = day(1).AddHours(8), End = day(1).AddHours(11), Priority = "zemas", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                new() { Id = 8, TechnicianId = 7, Title = "Rysio balansavimas", Location = "Kaunas", Start = day(1).AddHours(13), End = day(1).AddHours(19), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = false, Risk = "aukstas" },
                new() { Id = 9, TechnicianId = 8, Title = "Routerio konfigur.", Location = "Vilnius", Start = day(1).AddHours(10), End = day(1).AddHours(12), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                // Day 2 (Wed)
                new() { Id = 10, TechnicianId = 9, Title = "Alytaus apziura", Location = "Alytus", Start = day(2).AddHours(9), End = day(2).AddHours(13), Priority = "vidutinis", Status = "planuojama", IsLoad = true, IsConflict = false, IsTravelSwitch = true, Risk = "vidutinis" },
                new() { Id = 11, TechnicianId = 10, Title = "Klaipedos uzsakymas", Location = "Klaipeda", Start = day(2).AddHours(8), End = day(2).AddHours(17), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = false, Risk = "aukstas" },
                new() { Id = 12, TechnicianId = 11, Title = "Vilniaus auditavimas", Location = "Vilnius", Start = day(2).AddHours(14), End = day(2).AddHours(17), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                // Day 3 (Thu)
                new() { Id = 13, TechnicianId = 12, Title = "Kauno servisas", Location = "Kaunas", Start = day(3).AddHours(8), End = day(3).AddHours(12), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = true, Risk = "vidutinis" },
                new() { Id = 14, TechnicianId = 13, Title = "Siauliu migracija", Location = "Siauliai", Start = day(3).AddHours(11), End = day(3).AddHours(18), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = false, Risk = "aukstas" },
                new() { Id = 15, TechnicianId = 14, Title = "Vilniaus perjungimas", Location = "Vilnius", Start = day(3).AddHours(9), End = day(3).AddHours(17), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = true, Risk = "aukstas" },
                // Day 4 (Fri)
                new() { Id = 16, TechnicianId = 15, Title = "Kauno patikra", Location = "Kaunas", Start = day(4).AddHours(8), End = day(4).AddHours(12), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "zemas" },
                new() { Id = 17, TechnicianId = 5, Title = "Panevezio testai", Location = "Panevezys", Start = day(4).AddHours(13), End = day(4).AddHours(17), Priority = "vidutinis", Status = "planuojama", IsLoad = true, IsConflict = false, IsTravelSwitch = false, Risk = "vidutinis" },
                new() { Id = 18, TechnicianId = 6, Title = "Vilniaus incidentas", Location = "Vilnius", Start = day(4).AddHours(11), End = day(4).AddHours(19), Priority = "aukstas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = false, Risk = "aukstas" }
            };

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var availableStatuses = new List<string> { "Visi", "Normali apkrova", "Apkrova", "Perkrova" };

            if (!string.IsNullOrWhiteSpace(location))
            {
                tasks = tasks.Where(t => string.Equals(t.Location, location, StringComparison.OrdinalIgnoreCase)).ToList();
                technicians = technicians.Where(t => string.Equals(t.Location, location, StringComparison.OrdinalIgnoreCase) || tasks.Any(x => x.TechnicianId == t.Id)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var statusKey = status.ToLowerInvariant();
                tasks = statusKey switch
                {
                    "normali apkrova" => tasks.Where(t => !t.IsLoad && !t.IsConflict).ToList(),
                    "apkrova" => tasks.Where(t => t.IsLoad && !t.IsConflict).ToList(),
                    "perkrova" => tasks.Where(t => t.IsConflict || t.IsLoad).ToList(),
                    _ => tasks
                };

                technicians = technicians.Where(t => tasks.Any(x => x.TechnicianId == t.Id)).ToList();
            }

            var decisions = new List<DecisionOption>
            {
                new() { Title = "Perkelti Klaipedos uzduoti", Description = "Atideti Klaipedos perjungima i ryto langa.", RiskLevel = "vidutinis", LoadImpact = "-2h Vilnius", TravelImpact = "+1 kelione", IsRecommended = true },
                new() { Title = "Paskirstyti apkrova", Description = "Dali Kauno darbu perleisti Gabijai.", RiskLevel = "zemas", LoadImpact = "-1h Kaunas, +1h Siauliai", TravelImpact = "0", IsRecommended = false },
                new() { Title = "Palikti kaip yra", Description = "Jokiu pakeitimu - stebeti konfliktus.", RiskLevel = "aukstas", LoadImpact = "0", TravelImpact = "0", IsRecommended = false }
            };

            return new SchedulePageViewModel
            {
                WeekStart = weekStart,
                SelectedDate = selectedDate,
                IsManager = true,
                ViewMode = viewMode,
                Technicians = technicians,
                Tasks = tasks,
                DecisionOptions = decisions,
                AvailableLocations = availableLocations,
                AvailableStatuses = availableStatuses
            };
        }
    }
}
