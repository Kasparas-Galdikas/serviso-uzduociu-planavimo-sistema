using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Services.Authorization;
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

        public TasksController(ScheduleDbContext db, IPlanService planService, ITaskPermissionService taskPermissionService, ILogger<TasksController> logger)
        {
            _db = db;
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _logger = logger;
        }

        public class TaskEditDto
        {
            public string Title { get; set; } = string.Empty;
            public string Location { get; set; } = string.Empty;
            public DateTime StartAt { get; set; }
            public DateTime EndAt { get; set; }
            public TaskPriority Priority { get; set; }
            public ScheduleTaskStatus Status { get; set; }
        }

        public class TaskMoveDto
        {
            public int TargetTechnicianId { get; set; }
            public string TargetDate { get; set; } = string.Empty; // yyyy-MM-dd
            public int TargetSlotStartMinutes { get; set; }
            public uint ExpectedVersion { get; set; }
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
            _logger.LogInformation("api.task.get id={Id} editable={Editable}", id, isEditable);

            return Ok(new
            {
                task.Id,
                version,
                task.PlanId,
                task.TechnicianId,
                task.Title,
                task.Location,
                task.StartAt,
                task.EndAt,
                task.Priority,
                task.Status,
                planStatus = task.Plan?.Status.ToString(),
                isEditable
            });
        }

        [HttpPatch("{id:long}")]
        public async Task<IActionResult> UpdateTask(long id, [FromBody] TaskEditDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            try
            {
                var role = (Request.Query["role"].ToString() ?? string.Empty).ToLowerInvariant();
                var updated = await _planService.UpdateTaskAsync(id, task =>
                {
                    task.Title = dto.Title;
                    task.Location = dto.Location;
                    task.StartAt = dto.StartAt;
                    task.EndAt = dto.EndAt;
                    task.Priority = dto.Priority;
                    task.Status = dto.Status;
                }, role, cancellationToken);

                _logger.LogInformation("api.task.update id={Id} planId={PlanId}", id, updated.PlanId);
                return Ok(new { updated.Id });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "api.task.update.conflict id={Id}", id);
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpPatch("{id:long}/move")]
        public async Task<IActionResult> MoveTask(long id, [FromBody] TaskMoveDto dto, CancellationToken cancellationToken)
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
            var result = await _planService.MoveTaskAsync(
                id,
                dto.TargetTechnicianId,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.ExpectedVersion,
                role,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                return Ok(new
                {
                    result.Task.Id,
                    result.Task.TechnicianId,
                    result.Task.StartAt,
                    result.Task.EndAt,
                    version = result.CurrentVersion
                });
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
    }
}
