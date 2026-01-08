using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace ServiceTaskPlanningSystem.Controllers
{
    [ApiController]
    [Route("tasks")]
    public class TasksController : ControllerBase
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanService _planService;
        private readonly ILogger<TasksController> _logger;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;

        public TasksController(ScheduleDbContext db, IPlanService planService, ITaskPermissionService taskPermissionService, IScheduleTimeService timeService, ILogger<TasksController> logger)
        {
            _db = db;
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            var role = (Request.Query["role"].ToString() ?? string.Empty).ToLowerInvariant();
            var result = await _planService.CreateTaskAsync(
                dto.TechnicianId,
                targetDate,
                dto.StartMinutes,
                dto.EndMinutes,
                dto.Title,
                dto.Location,
                dto.Priority,
                dto.Status,
                role,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var version = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.Id == result.Task.Id)
                    .Select(t => EF.Property<uint>(t, "xmin"))
                    .FirstAsync(cancellationToken);

                var response = new TaskCreatedOrUpdatedResponseDto
                {
                    Id = result.Task.Id,
                    Version = version
                };

                _logger.LogInformation("api.task.create id={Id} planId={PlanId}", result.Task.Id, result.Task.PlanId);
                return Ok(response);
            }

            return result.Error switch
            {
                TaskCreateError.Overlap => Conflict(new { message = result.Message }),
                TaskCreateError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskCreateError.DraftNotFound => StatusCode(403, new { message = result.Message }),
                TaskCreateError.TechnicianNotFound => UnprocessableEntity(new { message = result.Message }),
                _ => UnprocessableEntity(new { message = result.Message ?? "Netinkamas laiko langas." })
            };
        }

        [HttpGet("{id:long}")]
        public async Task<IActionResult> GetTask(long id, CancellationToken cancellationToken)
        {
            var task = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (task == null)
            {
                return NotFound();
            }

            var version = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);

            var role = (Request.Query["role"].ToString() ?? string.Empty).ToLowerInvariant();
            var isEditable = await _taskPermissionService.CanEditTaskAsync(task, role, cancellationToken);

            var timezoneOffset = GetBrowserTimezoneOffset();
            
            // Convert UTC times from database to local time for frontend
            var localStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffset);
            var localEndAt = _timeService.ConvertUtcToLocal(task.EndAt, timezoneOffset);

            var response = new TaskDetailResponseDto
            {
                Id = task.Id,
                Version = version,
                PlanId = task.PlanId,
                TechnicianId = task.TechnicianId,
                Title = task.Title,
                Location = task.Location,
                StartAt = localStartAt,
                EndAt = localEndAt,
                Priority = task.Priority,
                Status = task.Status,
                PlanStatus = task.Plan?.Status.ToString() ?? string.Empty,
                IsEditable = isEditable,
                TimezoneOffsetMinutes = timezoneOffset
            };

            _logger.LogInformation("api.task.get id={Id} editable={Editable} tz={Tz} utcStart={UtcStart} localStart={LocalStart}", id, isEditable, timezoneOffset, task.StartAt, localStartAt);
            return Ok(response);
        }

        [HttpPatch("{id:long}")]
        public async Task<IActionResult> UpdateTask(long id, [FromBody] UpdateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            try
            {
                // Convert local times from frontend to UTC for storage
                var utcStartAt = _timeService.ConvertLocalToUtc(dto.StartAt, dto.TimezoneOffsetMinutes);
                var utcEndAt = _timeService.ConvertLocalToUtc(dto.EndAt, dto.TimezoneOffsetMinutes);

                _logger.LogInformation(
                    "api.task.update.request id={Id} tz={Tz} localStart={LocalStart} utcStart={UtcStart}",
                    id, dto.TimezoneOffsetMinutes, dto.StartAt, utcStartAt);

                var role = (Request.Query["role"].ToString() ?? string.Empty).ToLowerInvariant();
                var updated = await _planService.UpdateTaskAsync(id, task =>
                {
                    task.Title = dto.Title;
                    task.Location = dto.Location;
                    task.StartAt = utcStartAt;
                    task.EndAt = utcEndAt;
                    task.Priority = dto.Priority;
                    task.Status = dto.Status;
                }, role, cancellationToken);

                var version = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.Id == id)
                    .Select(t => EF.Property<uint>(t, "xmin"))
                    .FirstAsync(cancellationToken);

                var response = new TaskCreatedOrUpdatedResponseDto
                {
                    Id = updated.Id,
                    Version = version
                };

                _logger.LogInformation("api.task.update id={Id} planId={PlanId} version={Version}", id, updated.PlanId, version);
                return Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.task.update.conflict id={Id}", id);
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpPatch("{id:long}/move")]
        public async Task<IActionResult> MoveTask(long id, [FromBody] MoveTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            _logger.LogInformation(
                "api.task.move.request id={Id} tz={Tz} targetDate={TargetDate}",
                id, dto.TimezoneOffsetMinutes, dto.TargetDate);

            var role = (Request.Query["role"].ToString() ?? string.Empty).ToLowerInvariant();
            var result = await _planService.MoveTaskAsync(
                id,
                (int)dto.TargetTechnicianId,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.Version,
                role,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var version = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.Id == id)
                    .Select(t => EF.Property<uint>(t, "xmin"))
                    .FirstAsync(cancellationToken);

                // Convert UTC times to local for response
                var timezoneOffset = dto.TimezoneOffsetMinutes;
                var localStartAt = _timeService.ConvertUtcToLocal(result.Task.StartAt, timezoneOffset);
                var localEndAt = _timeService.ConvertUtcToLocal(result.Task.EndAt, timezoneOffset);

                var response = new TaskMovedResponseDto
                {
                    Id = result.Task.Id,
                    Version = version,
                    TechnicianId = result.Task.TechnicianId,
                    StartAt = localStartAt,
                    EndAt = localEndAt
                };

                _logger.LogInformation("api.task.move id={Id} techId={TechId} version={Version}", id, dto.TargetTechnicianId, version);
                return Ok(response);
            }

            return result.Error switch
            {
                TaskMoveError.NotFound => NotFound(new { message = result.Message }),
                TaskMoveError.TechnicianNotFound => UnprocessableEntity(new { message = result.Message }),
                TaskMoveError.InvalidWindow => UnprocessableEntity(new { message = result.Message }),
                TaskMoveError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskMoveError.Overlap => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                TaskMoveError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = result.Message ?? "Unknown error" })
            };
        }

        private static bool TryParseDate(string value, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                parsed = dt.Date;
                return true;
            }

            parsed = default;
            return false;
        }

        /// <summary>
        /// Extract browser timezone offset from request headers.
        /// Default to 0 if not provided.
        /// </summary>
        private int GetBrowserTimezoneOffset()
        {
            if (Request.Headers.TryGetValue("X-Timezone-Offset", out var tzHeader) &&
                int.TryParse(tzHeader.ToString(), out var offset))
            {
                return Math.Abs(offset);
            }
            return 0;
        }
    }
}
