using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.Authorization;

namespace ServiceTaskPlanningSystem.Controllers.UI
{
    [Route("Schedule")]
    public class ScheduleViewController : Controller
    {
        private readonly IScheduleProvider _scheduleProvider;
        private readonly IScheduleLayoutBuilder _layoutBuilder;
        private readonly IPlanService _planService;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<ScheduleViewController> _logger;
        private readonly IUserContextResolver _userContextResolver;

        public ScheduleViewController(IScheduleProvider scheduleProvider, IScheduleLayoutBuilder layoutBuilder, IPlanService planService, IScheduleTimeService timeService, IUserContextResolver userContextResolver, ILogger<ScheduleViewController> logger)
        {
            _scheduleProvider = scheduleProvider;
            _layoutBuilder = layoutBuilder;
            _planService = planService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
            _logger = logger;
        }

        [HttpGet("", Name = "ScheduleView")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(string view = "day", string? date = null, string? location = null, string? workload = null, string? status = null, string? info = null, string? planScope = null, long? technicianId = null)
        {
            var activePlan = await _planService.GetActivePlanAsync();
            var draftPlan = await _planService.GetDraftPlanAsync();
            var user = _userContextResolver.GetCurrentUser();
            var isManager = user.IsManager;

            if (draftPlan != null && activePlan == null)
            {
                // Invalid state: draft should originate from an active plan. Log/flag as needed.
            }

            var timezoneOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var vm = await _scheduleProvider.GetScheduleAsync(view, date, location, workload, status, info, planScope, technicianId, activePlan, draftPlan, user, timezoneOffset);
            vm.IsManager = isManager;
            vm.CurrentUserId = user.IsTechnician ? user.TechnicianId : user.UserId;
            vm.EarliestStartMinutes = _timeService.GetEarliestCreatableStartMinutes(
                _timeService.ConvertUtcToLocal(_timeService.GetCurrentUtcTime(), timezoneOffset).Date,
                timezoneOffset, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes);
            if (!vm.IsManager && vm.Tasks != null)
            {
                foreach (var t in vm.Tasks)
                {
                    t.IsEditable = false;
                }
            }
            vm = EnrichWithLayout(vm, timezoneOffset);
            _logger.LogInformation("api.schedule.index {View} {Date} {Location} {Workload} active={Active} activePeriod={ActiveStart}-{ActiveEnd} activeStatus={ActiveStatus} draft={Draft} draftPeriod={DraftStart}-{DraftEnd} draftStatus={DraftStatus}",
                vm.ViewMode,
                vm.SelectedDate,
                location ?? "all",
                workload ?? "all",
                activePlan?.Id,
                activePlan?.PeriodStart,
                activePlan?.PeriodEnd,
                activePlan?.Status.ToString(),
                draftPlan?.Id,
                draftPlan?.PeriodStart,
                draftPlan?.PeriodEnd,
                draftPlan?.Status.ToString());
            return View("~/Views/Schedule/Index.cshtml", vm);
        }

        [HttpGet("Table", Name = "ScheduleTable")]
        public async Task<IActionResult> Table(string view = "day", string? date = null, string? location = null, string? workload = null, string? status = null, string? info = null, string? planScope = null, long? technicianId = null)
        {
            var activePlan = await _planService.GetActivePlanAsync();
            var draftPlan = await _planService.GetDraftPlanAsync();
            var user = _userContextResolver.GetCurrentUser();
            var isManager = user.IsManager;

            if (draftPlan != null && activePlan == null)
            {
                // Invalid state: draft should originate from an active plan. Log/flag as needed.
            }

            var timezoneOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var vm = await _scheduleProvider.GetScheduleAsync(view, date, location, workload, status, info, planScope, technicianId, activePlan, draftPlan, user, timezoneOffset);
            vm.IsManager = isManager;
            vm.CurrentUserId = user.IsTechnician ? user.TechnicianId : user.UserId;
            vm.EarliestStartMinutes = _timeService.GetEarliestCreatableStartMinutes(
                _timeService.ConvertUtcToLocal(_timeService.GetCurrentUtcTime(), timezoneOffset).Date,
                timezoneOffset, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes);
            if (!vm.IsManager && vm.Tasks != null)
            {
                foreach (var t in vm.Tasks)
                {
                    t.IsEditable = false;
                }
            }
            vm = EnrichWithLayout(vm, timezoneOffset);
            var partial = string.Equals(vm.ViewMode, "week", StringComparison.OrdinalIgnoreCase)
                ? "~/Views/Schedule/Partials/Tables/_WeeklyTable.cshtml"
                : "~/Views/Schedule/Partials/Tables/_DailyTable.cshtml";

            _logger.LogInformation("api.schedule.table {View} {Date} {Location} {Workload} tasks={TaskCount} issues={Issues} active={Active} activePeriod={ActiveStart}-{ActiveEnd} activeStatus={ActiveStatus} draft={Draft} draftPeriod={DraftStart}-{DraftEnd} draftStatus={DraftStatus}",
                vm.ViewMode,
                vm.SelectedDate,
                location ?? "all",
                workload ?? "all",
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

        private SchedulePageViewModel EnrichWithLayout(SchedulePageViewModel viewModel, int timezoneOffsetMinutes)
        {
            if (string.Equals(viewModel.ViewMode, "week", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.WeeklyLayout = _layoutBuilder.BuildWeeklyLayout(viewModel, timezoneOffsetMinutes);
            }
            else
            {
                viewModel.DailyLayout = _layoutBuilder.BuildDailyLayout(viewModel, timezoneOffsetMinutes);
            }

            return viewModel;
        }
    }
}
