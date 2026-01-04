using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Services.Schedule;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class ScheduleController : Controller
    {
        private readonly IScheduleProvider _scheduleProvider;
        private readonly IScheduleLayoutBuilder _layoutBuilder;
        private readonly IPlanService _planService;

        public ScheduleController(IScheduleProvider scheduleProvider, IScheduleLayoutBuilder layoutBuilder, IPlanService planService)
        {
            _scheduleProvider = scheduleProvider;
            _layoutBuilder = layoutBuilder;
            _planService = planService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var activePlan = await _planService.GetActivePlanAsync();
            var draftPlan = await _planService.GetDraftPlanAsync();

            if (draftPlan != null && activePlan == null)
            {
                // Invalid state: draft should originate from an active plan. Log/flag as needed.
            }

            var vm = _scheduleProvider.GetSchedule(view, date, location, status, activePlan, draftPlan);
            vm.IsManager = true; // mock: assume manager is logged in
            vm = EnrichWithLayout(vm);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Table(string view = "day", string? date = null, string? location = null, string? status = null)
        {
            var activePlan = await _planService.GetActivePlanAsync();
            var draftPlan = await _planService.GetDraftPlanAsync();

            if (draftPlan != null && activePlan == null)
            {
                // Invalid state: draft should originate from an active plan. Log/flag as needed.
            }

            var vm = _scheduleProvider.GetSchedule(view, date, location, status, activePlan, draftPlan);
            vm.IsManager = true; // mock: assume manager is logged in
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
