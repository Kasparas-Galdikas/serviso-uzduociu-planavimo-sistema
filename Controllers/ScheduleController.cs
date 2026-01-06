using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Services.Schedule;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class ScheduleController : Controller
    {
        private readonly IScheduleProvider _scheduleProvider;
        private readonly IScheduleLayoutBuilder _layoutBuilder;
        private readonly IPlanService _planService;
        private readonly ILogger<ScheduleController> _logger;

        public ScheduleController(IScheduleProvider scheduleProvider, IScheduleLayoutBuilder layoutBuilder, IPlanService planService, ILogger<ScheduleController> logger)
        {
            _scheduleProvider = scheduleProvider;
            _layoutBuilder = layoutBuilder;
            _planService = planService;
            _logger = logger;
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

            var vm = await _scheduleProvider.GetScheduleAsync(view, date, location, status, activePlan, draftPlan);
            vm.IsManager = true; // mock: assume manager is logged in
            vm = EnrichWithLayout(vm);
            _logger.LogInformation("api.schedule.index {View} {Date} {Location} {Status} active={Active} activePeriod={ActiveStart}-{ActiveEnd} activeStatus={ActiveStatus} draft={Draft} draftPeriod={DraftStart}-{DraftEnd} draftStatus={DraftStatus}",
                vm.ViewMode,
                vm.SelectedDate,
                location ?? "all",
                status ?? "all",
                activePlan?.Id,
                activePlan?.PeriodStart,
                activePlan?.PeriodEnd,
                activePlan?.Status.ToString(),
                draftPlan?.Id,
                draftPlan?.PeriodStart,
                draftPlan?.PeriodEnd,
                draftPlan?.Status.ToString());
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

            var vm = await _scheduleProvider.GetScheduleAsync(view, date, location, status, activePlan, draftPlan);
            vm.IsManager = true; // mock: assume manager is logged in
            vm = EnrichWithLayout(vm);
            var partial = string.Equals(vm.ViewMode, "week", StringComparison.OrdinalIgnoreCase)
                ? "~/Views/Schedule/Partials/Tables/_WeeklyTable.cshtml"
                : "~/Views/Schedule/Partials/Tables/_DailyTable.cshtml";

            _logger.LogInformation("api.schedule.table {View} {Date} {Location} {Status} tasks={TaskCount} issues={Issues} active={Active} activePeriod={ActiveStart}-{ActiveEnd} activeStatus={ActiveStatus} draft={Draft} draftPeriod={DraftStart}-{DraftEnd} draftStatus={DraftStatus}",
                vm.ViewMode,
                vm.SelectedDate,
                location ?? "all",
                status ?? "all",
                vm.Tasks?.Count ?? 0,
                vm.Issues?.Count ?? 0,
                activePlan?.Id,
                activePlan?.PeriodStart,
                activePlan?.PeriodEnd,
                activePlan?.Status.ToString(),
                draftPlan?.Id,
                draftPlan?.PeriodStart,
                draftPlan?.PeriodEnd,
                draftPlan?.Status.ToString());
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
