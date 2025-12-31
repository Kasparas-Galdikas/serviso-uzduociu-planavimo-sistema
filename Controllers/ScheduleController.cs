using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Schedule;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class ScheduleController : Controller
    {
        private readonly IScheduleProvider _scheduleProvider;
        private readonly IScheduleLayoutBuilder _layoutBuilder;

        public ScheduleController(IScheduleProvider scheduleProvider, IScheduleLayoutBuilder layoutBuilder)
        {
            _scheduleProvider = scheduleProvider;
            _layoutBuilder = layoutBuilder;
        }

        [HttpGet]
        public IActionResult Index(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var vm = _scheduleProvider.GetSchedule(view, date, location, status);
            vm = EnrichWithLayout(vm);
            return View(vm);
        }

        [HttpGet]
        public IActionResult Table(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var vm = _scheduleProvider.GetSchedule(view, date, location, status);
            vm = EnrichWithLayout(vm);
            var partial = string.Equals(vm.ViewMode, "week", StringComparison.OrdinalIgnoreCase)
                ? "~/Views/Schedule/Partials/Tables/_WeeklyTable.cshtml"
                : "~/Views/Schedule/Partials/Tables/_DailyTable.cshtml";

            return PartialView(partial, vm);
        }

        private SchedulePageViewModel EnrichWithLayout(SchedulePageViewModel viewModel)
        {
            if (string.Equals(viewModel.ViewMode, "week", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.WeeklyLayout = _layoutBuilder.BuildWeeklyLayout(viewModel);
            }
            else
            {
                viewModel.DailyLayout = _layoutBuilder.BuildDailyLayout(viewModel);
            }

            return viewModel;
        }
    }
}
