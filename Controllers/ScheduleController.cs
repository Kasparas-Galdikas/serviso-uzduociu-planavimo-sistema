using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class ScheduleController : Controller
    {
        [HttpGet]
        public IActionResult Index(string view = "day", string? date = null)
        {
            var selectedDate = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
            {
                selectedDate = parsedDate.Date;
            }

            var weekStart = selectedDate.AddDays(-(int)selectedDate.DayOfWeek + (int)DayOfWeek.Monday);
            var viewMode = string.Equals(view, "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day";

            var technicians = new List<Technician>
            {
                new() { Id = 1, Name = "Austėja J.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h, savaitės 40h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
                new() { Id = 2, Name = "Dovydas K.", Location = "Kaunas", RulesSummary = "Dienos limitas 10h, savaitės 45h", WeeklyHours = 22, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 10, TravelSwitches = 1 },
                new() { Id = 3, Name = "Gabija P.", Location = "Šiauliai", RulesSummary = "Dienos limitas 9h, savaitės 42h", WeeklyHours = 15, WeeklyLimit = 42, DailyHours = 5, DailyLimit = 9, TravelSwitches = 3 }
            };

            var tasks = new List<TaskItem>
            {
                new() { Id = 1, TechnicianId = 1, Title = "Kabelių patikra", Location = "Vilnius", Start = selectedDate.AddHours(9), End = selectedDate.AddHours(11), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "žemas" },
                new() { Id = 2, TechnicianId = 1, Title = "Serverio diagnostika", Location = "Vilnius", Start = selectedDate.AddHours(12), End = selectedDate.AddHours(14), Priority = "aukštas", Status = "planuojama", IsLoad = true, IsConflict = false, IsTravelSwitch = false, Risk = "vidutinis" },
                new() { Id = 3, TechnicianId = 1, Title = "Tinklo perjungimas", Location = "Klaipėda", Start = selectedDate.AddHours(16), End = selectedDate.AddHours(18), Priority = "aukštas", Status = "planuojama", IsLoad = false, IsConflict = true, IsTravelSwitch = true, Risk = "aukštas" },

                new() { Id = 4, TechnicianId = 2, Title = "Maršrutizatoriaus konfig.", Location = "Kaunas", Start = selectedDate.AddHours(9), End = selectedDate.AddHours(11), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "žemas" },
                new() { Id = 5, TechnicianId = 2, Title = "Wifi auditavimas", Location = "Panevėžys", Start = selectedDate.AddHours(12), End = selectedDate.AddHours(15), Priority = "aukštas", Status = "planuojama", IsLoad = true, IsConflict = true, IsTravelSwitch = true, Risk = "aukštas" },
                new() { Id = 6, TechnicianId = 2, Title = "UPS testas", Location = "Kaunas", Start = selectedDate.AddHours(17), End = selectedDate.AddHours(19), Priority = "žemas", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "žemas" },

                new() { Id = 7, TechnicianId = 3, Title = "Šviesolaidžio matavimai", Location = "Šiauliai", Start = selectedDate.AddHours(8), End = selectedDate.AddHours(11), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = false, Risk = "vidutinis" },
                new() { Id = 8, TechnicianId = 3, Title = "Kelionė į Klaipėdą", Location = "Klaipėda", Start = selectedDate.AddHours(14), End = selectedDate.AddHours(17), Priority = "vidutinis", Status = "planuojama", IsLoad = false, IsConflict = false, IsTravelSwitch = true, Risk = "vidutinis" }
            };

            var decisions = new List<DecisionOption>
            {
                new() { Title = "Perkelti Klaipėdos užduotį", Description = "Atidėti Klaipėdos perjungimą į ryto langą.", RiskLevel = "vidutinis", LoadImpact = "-2h Vilnius", TravelImpact = "+1 kelionė", IsRecommended = true },
                new() { Title = "Paskirstyti apkrovą", Description = "Dalį Kauno darbų perleisti Gabijai.", RiskLevel = "žemas", LoadImpact = "-1h Kaunas, +1h Šiauliai", TravelImpact = "0", IsRecommended = false },
                new() { Title = "Palikti kaip yra", Description = "Jokių pakeitimų – stebėti konfliktus.", RiskLevel = "aukštas", LoadImpact = "0", TravelImpact = "0", IsRecommended = false }
            };

            var vm = new SchedulePageViewModel
            {
                WeekStart = weekStart,
                SelectedDate = selectedDate,
                IsManager = true,
                ViewMode = viewMode,
                Technicians = technicians,
                Tasks = tasks,
                DecisionOptions = decisions
            };

            return View(vm);
        }
    }
}
