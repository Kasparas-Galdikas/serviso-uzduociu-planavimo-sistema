using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Plans;

namespace ServiceTaskPlanningSystem.Controllers
{
    [ApiController]
    [Route("tasks")]
    public class TasksController : ControllerBase
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanService _planService;

        public TasksController(ScheduleDbContext db, IPlanService planService)
        {
            _db = db;
            _planService = planService;
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

            var isEditable = task.Plan?.Status == PlanStatus.Draft;

            return Ok(new
            {
                task.Id,
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
                var updated = await _planService.UpdateTaskAsync(id, task =>
                {
                    task.Title = dto.Title;
                    task.Location = dto.Location;
                    task.StartAt = dto.StartAt;
                    task.EndAt = dto.EndAt;
                    task.Priority = dto.Priority;
                    task.Status = dto.Status;
                }, cancellationToken);

                return Ok(new { updated.Id });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }
    }
}
