using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ServiceTaskPlanningSystem.Services.Scheduling;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/tasks")]
    public class TasksController : ControllerBase
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanService _planService;
        private readonly ILogger<TasksController> _logger;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly IUserContextResolver _userContextResolver;

        public TasksController(ScheduleDbContext db, IPlanService planService, ITaskPermissionService taskPermissionService, IScheduleTimeService timeService, IUserContextResolver userContextResolver, ILogger<TasksController> logger)
        {
            _db = db;
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
            _logger = logger;
        }

        private static DateTime GetWeekStartUtc(DateTime dateUtc)
        {
            var dayOfWeek = dateUtc.DayOfWeek;
            var diff = dayOfWeek - DayOfWeek.Monday;
            if (diff < 0) diff += 7;
            return dateUtc.AddDays(-diff).Date;
        }

        private static LoadSnapshotDto ToLoadSnapshotDto(TechnicianEntity technician, WorkloadSnapshot snapshot)
        {
            var context = snapshot.Context;
            return new LoadSnapshotDto
            {
                LoadState = context.ComputeLoadState().ToString(),
                HasConflict = context.HasConflict,
                IsDailyLimitExceeded = context.IsDailyLimitExceeded,
                IsWeeklyLimitExceeded = context.IsWeeklyLimitExceeded,
                IsSwitchLimitExceeded = context.IsSwitchLimitExceeded,
                DailyHours = Math.Round(snapshot.DailyHours, 2),
                WeeklyHours = Math.Round(snapshot.WeeklyHours, 2),
                DailyLimit = (double)technician.DailyLimit,
                WeeklyLimit = (double)technician.WeeklyLimit,
                DailyUtilization = Math.Round(context.DailyUtilization, 2),
                WeeklyUtilization = Math.Round(context.WeeklyUtilization, 2),
                SwitchCount = snapshot.SwitchCount
            };
        }

        private static LoadSnapshotDto BuildLoadSnapshotForSimulation(
            long technicianId,
            DateTime dayStartUtc,
            List<TaskEntity> tasks,
            List<TechnicianEntity> technicians)
        {
            var technician = technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
            {
                return new LoadSnapshotDto();
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = GetWeekStartUtc(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var dayTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= dayStartUtc &&
                            t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var weekTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks);
            return ToLoadSnapshotDto(technician, snapshot);
        }

        private async Task<LoadSnapshotDto> BuildLoadSnapshotAsync(long technicianId, DateTime dayStartUtc, CancellationToken cancellationToken)
        {
            var technician = await _db.Technicians
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

            if (technician == null)
            {
                return new LoadSnapshotDto();
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = GetWeekStartUtc(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var weekTasks = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var dayTasks = weekTasks
                .Where(t => t.StartAt >= dayStartUtc && t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks);
            return ToLoadSnapshotDto(technician, snapshot);
        }

        [HttpPost]
        [Authorize(Policy = "CanEditTask")]
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

            if (dto.TechnicianId <= 0)
            {
                return UnprocessableEntity(new { message = "Priskirtas darbuotojas yra privalomas." });
            }

            if (string.IsNullOrWhiteSpace(dto.SkillLevel) ||
                string.IsNullOrWhiteSpace(dto.ObjectName) ||
                string.IsNullOrWhiteSpace(dto.ObjectAddress) ||
                string.IsNullOrWhiteSpace(dto.Location))
            {
                return UnprocessableEntity(new { message = "Privalomi laukai (kvalifikacija, objektas ir adresas) turi buti uzpildyti." });
            }

            var normalizedSkillLevel = dto.SkillLevel.Trim();
            var normalizedObjectName = dto.ObjectName.Trim();
            var normalizedObjectAddress = dto.ObjectAddress.Trim();
            var normalizedLocation = dto.Location.Trim();

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.CreateTaskAsync(
                dto.TechnicianId,
                targetDate,
                dto.StartMinutes,
                dto.EndMinutes,
                dto.Title,
                normalizedLocation,
                dto.Priority,
                dto.Status,
                normalizedSkillLevel,
                normalizedObjectName,
                normalizedObjectAddress,
                dto.Instructions ?? string.Empty,
                user,
                dto.TimezoneOffsetMinutes,
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
                    Version = version,
                    Load = await BuildLoadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };

                _logger.LogInformation("api.task.create id={Id} planId={PlanId}", result.Task.Id, result.Task.PlanId);
                return Ok(response);
            }

            return result.Error switch
            {
                TaskCreateError.Overlap => Conflict(new { message = result.Message }),
                TaskCreateError.NotEditable => StatusCode(403, new { message = result.Message }),
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
                .Include(t => t.Object)
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

            var timezoneOffset = GetBrowserTimezoneOffset();
            var user = _userContextResolver.GetCurrentUser();
            var isEditable = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffset);

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
                TechnicianComment = task.TechnicianComment,
                SkillLevel = task.SkillLevel,
                ObjectId = task.ObjectId,
                ObjectName = task.Object?.Name ?? string.Empty,
                ObjectAddress = task.Object?.Address ?? string.Empty,
                Instructions = task.Instructions,
                PlanStatus = task.Plan?.Status.ToString() ?? string.Empty,
                IsEditable = isEditable,
                TimezoneOffsetMinutes = timezoneOffset
            };

            _logger.LogInformation("api.task.get id={Id} editable={Editable} tz={Tz} utcStart={UtcStart} localStart={LocalStart}", id, isEditable, timezoneOffset, task.StartAt, localStartAt);
            return Ok(response);
        }

        [HttpDelete("{id:long}")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> DeleteTask(long id, [FromBody] DeleteTaskRequestDto dto, CancellationToken cancellationToken)
        {
            _logger.LogInformation("api.task.delete.requested id={Id} version={Version}", id, dto.Version);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("api.task.delete.validation-failed id={Id}", id);
                return ValidationProblem(ModelState);
            }

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.DeleteTaskAsync(id, dto.Version, user, dto.TimezoneOffsetMinutes, cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var response = new TaskDeletedResponseDto
                {
                    DeletedTaskId = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    Load = await BuildLoadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };
                _logger.LogInformation("api.task.delete.succeeded id={Id}", id);
                return Ok(response);
            }

            _logger.LogWarning("api.task.delete.rejected id={Id} reason={Reason}", id, result.Error);

            return result.Error switch
            {
                TaskDeleteError.NotFound => NotFound(new { message = result.Message }),
                TaskDeleteError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskDeleteError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = "Nepavyko istrinti uzduoties." })
            };
        }

        [HttpPatch("{id:long}/time")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> UpdateTaskTime(long id, [FromBody] UpdateTaskTimeRequestDto dto, CancellationToken cancellationToken)
        {
            _logger.LogInformation("api.task.time.requested id={Id} start={Start} end={End} version={Version}", id, dto.TargetSlotStartMinutes, dto.TargetSlotEndMinutes, dto.Version);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("api.task.time.validation-failed id={Id}", id);
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.UpdateTaskTimeAsync(
                id,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.TargetSlotEndMinutes,
                dto.Version,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var localStart = _timeService.ConvertUtcToLocal(result.Task.StartAt, dto.TimezoneOffsetMinutes);
                var localEnd = _timeService.ConvertUtcToLocal(result.Task.EndAt, dto.TimezoneOffsetMinutes);
                var response = new TaskMovedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    TechnicianId = result.Task.TechnicianId,
                    StartAt = localStart,
                    EndAt = localEnd,
                    Load = await BuildLoadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };
                _logger.LogInformation("api.task.time.succeeded id={Id}", id);
                return Ok(response);
            }

            _logger.LogWarning("api.task.time.rejected id={Id} reason={Reason}", id, result.Error);

            return result.Error switch
            {
                TaskTimeUpdateError.NotFound => NotFound(new { message = result.Message }),
                TaskTimeUpdateError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskTimeUpdateError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                TaskTimeUpdateError.Overlap => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => UnprocessableEntity(new { message = result.Message ?? "Netinkamas laiko langas." })
            };
        }

        [HttpPost("preview/drag")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> PreviewDrag([FromBody] DragPreviewRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (dto.TargetEndAt <= dto.TargetStartAt)
            {
                return UnprocessableEntity(new { message = "Pabaigos laikas turi būti vėlesnis už pradžią." });
            }

            var timezoneOffset = GetBrowserTimezoneOffset();
            var user = _userContextResolver.GetCurrentUser();

            var targetTask = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.Technician)
                .FirstOrDefaultAsync(t => t.Id == dto.TaskId, cancellationToken);

            if (targetTask == null)
            {
                return NotFound();
            }

            var isEditable = await _taskPermissionService.CanEditTaskAsync(targetTask, user, timezoneOffset);
            if (!isEditable)
            {
                return StatusCode(403);
            }

            if (targetTask.PlanId == null)
            {
                return UnprocessableEntity(new { message = "Užduotis neturi plano." });
            }

            var hoverTech = await _db.Technicians
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == dto.TargetTechnicianId, cancellationToken);

            if (hoverTech == null)
            {
                return UnprocessableEntity(new { message = "Technikas nerastas." });
            }

            var dayStart = dto.TargetStartAt.Date;
            var weekStart = WeekCalculator.GetWeekStart(dayStart);
            var weekEndExclusive = weekStart.AddDays(7);

            var scopeTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Technician)
                .Where(t => t.PlanId == targetTask.PlanId &&
                            t.StartAt < weekEndExclusive &&
                            t.EndAt > weekStart)
                .ToListAsync(cancellationToken);

            var technicians = await _db.Technicians
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var simulation = PlanSimulationContext.Create(scopeTasks, technicians, targetTask);

            var modifiedSim = simulation.WithModifiedTasks(tasks =>
            {
                var modified = tasks.ToList();
                var idx = modified.FindIndex(t => t.Id == targetTask.Id);
                if (idx >= 0)
                {
                    modified[idx] = new TaskEntity
                    {
                        Id = targetTask.Id,
                        PlanId = targetTask.PlanId,
                        TechnicianId = dto.TargetTechnicianId,
                        Title = targetTask.Title,
                        Location = targetTask.Location,
                        RegionId = hoverTech.RegionId ?? targetTask.RegionId,
                        StartAt = dto.TargetStartAt,
                        EndAt = dto.TargetEndAt,
                        Priority = targetTask.Priority,
                        Status = targetTask.Status,
                        CreatedAt = targetTask.CreatedAt,
                        Plan = targetTask.Plan,
                        Technician = targetTask.Technician,
                        Region = targetTask.Region,
                        Evaluation = targetTask.Evaluation,
                        Object = targetTask.Object,
                        ObjectId = targetTask.ObjectId,
                        Instructions = targetTask.Instructions,
                        SkillLevel = targetTask.SkillLevel,
                        TechnicianComment = targetTask.TechnicianComment
                    };
                }
                return modified;
            });

            var visibleTechs = technicians
                .Where(t => scopeTasks.Any(task => task.TechnicianId == t.Id) || t.Id == dto.TargetTechnicianId)
                .ToList();

            var response = new DragPreviewResponseDto
            {
                DraggedTaskId = targetTask.Id
            };

            foreach (var tech in visibleTechs)
            {
                var loadSnapshot = BuildLoadSnapshotForSimulation(
                    tech.Id,
                    dayStart,
                    modifiedSim.Tasks.ToList(),
                    modifiedSim.Technicians.ToList());

                response.PreviewStates.Add(new DragPreviewStateDto
                {
                    TechnicianId = tech.Id,
                    LoadState = loadSnapshot.LoadState,
                    HasConflict = loadSnapshot.HasConflict,
                    IsDailyLimitExceeded = loadSnapshot.IsDailyLimitExceeded,
                    IsWeeklyLimitExceeded = loadSnapshot.IsWeeklyLimitExceeded,
                    IsSwitchLimitExceeded = loadSnapshot.IsSwitchLimitExceeded,
                    DailyHours = Math.Round(loadSnapshot.DailyHours, 2),
                    WeeklyHours = Math.Round(loadSnapshot.WeeklyHours, 2),
                    DailyLimit = loadSnapshot.DailyLimit,
                    WeeklyLimit = loadSnapshot.WeeklyLimit,
                    DailyUtilization = Math.Round(loadSnapshot.DailyUtilization, 2),
                    WeeklyUtilization = Math.Round(loadSnapshot.WeeklyUtilization, 2),
                    SwitchCount = loadSnapshot.SwitchCount
                });
            }

            _logger.LogInformation("api.task.preview.drag task={TaskId} plan={PlanId} tech={Tech} date={Day} states={Count}", targetTask.Id, targetTask.PlanId, dto.TargetTechnicianId, dayStart, response.PreviewStates.Count);

            return Ok(response);
        }

        [HttpPatch("{id:long}")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> UpdateTask(long id, [FromBody] UpdateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (dto.EndAt <= dto.StartAt)
            {
                return UnprocessableEntity(new { message = "Pabaigos laikas turi b?ti v?lesnis u? prad?i?." });
            }

            if (string.IsNullOrWhiteSpace(dto.SkillLevel) ||
                string.IsNullOrWhiteSpace(dto.ObjectName) ||
                string.IsNullOrWhiteSpace(dto.ObjectAddress) ||
                string.IsNullOrWhiteSpace(dto.Location))
            {
                return UnprocessableEntity(new { message = "Privalomi laukai (kvalifikacija, objektas ir adresas) turi buti uzpildyti." });
            }

            var normalizedSkillLevel = dto.SkillLevel.Trim();
            var normalizedObjectName = dto.ObjectName.Trim();
            var normalizedObjectAddress = dto.ObjectAddress.Trim();
            var normalizedLocation = dto.Location.Trim();
            var normalizedInstructions = (dto.Instructions ?? string.Empty).Trim();

            // Convert local times from frontend to UTC for storage
            var utcStartAt = _timeService.ConvertLocalToUtc(dto.StartAt, dto.TimezoneOffsetMinutes);
            var utcEndAt = _timeService.ConvertLocalToUtc(dto.EndAt, dto.TimezoneOffsetMinutes);

            _logger.LogInformation(
                "api.task.update.request id={Id} tz={Tz} localStart={LocalStart} utcStart={UtcStart}",
                id, dto.TimezoneOffsetMinutes, dto.StartAt, utcStartAt);

            var existing = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (existing == null)
            {
                return NotFound(new { message = "U?duotis nerasta." });
            }

            var currentVersion = _db.Entry(existing).Property<uint>("xmin").CurrentValue;
            if (dto.Version == 0 || dto.Version != currentVersion)
            {
                return Conflict(new { message = "U?duotis buvo pakeista kito vartotojo.", version = currentVersion });
            }

            var user = _userContextResolver.GetCurrentUser();
            if (user.IsTechnician && (!user.TechnicianId.HasValue || existing.TechnicianId != user.TechnicianId.Value))
            {
                return StatusCode(403, new { message = "Galite redaguoti tik savo u?duotis." });
            }

            if (!_timeService.IsTaskStillEditable(existing.EndAt))
            {
                return UnprocessableEntity(new { message = "?i u?duotis nebegali b?ti redaguojama." });
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(existing, user, dto.TimezoneOffsetMinutes);
            if (!canEdit)
            {
                return StatusCode(403, new { message = "Neturite teisi? atlikti ?io veiksmo." });
            }

            var technicianExists = await _db.Technicians
                .AsNoTracking()
                .AnyAsync(t => t.Id == dto.TechnicianId, cancellationToken);

            if (!technicianExists)
            {
                return UnprocessableEntity(new { message = "Technikas nerastas." });
            }

            // Validate working window and slot alignment in caller's local time
            var startLocal = dto.StartAt;
            var endLocal = dto.EndAt;
            if (startLocal.Date != endLocal.Date)
            {
                return UnprocessableEntity(new { message = "U?duotis turi tilpti vien? dien?." });
            }

            var startMinutes = startLocal.Hour * 60 + startLocal.Minute;
            var endMinutes = endLocal.Hour * 60 + endLocal.Minute;
            if (startMinutes < ScheduleGrid.DayStartMinutes || endMinutes > ScheduleGrid.DayEndMinutes)
            {
                return UnprocessableEntity(new { message = "U?duotis privalo tilpti ? darbo laik?." });
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0 || endMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return UnprocessableEntity(new { message = "Laikas turi atitikti grafiko ?ingsn?." });
            }

            var hasOverlap = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id != id)
                .Where(t => t.TechnicianId == dto.TechnicianId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < utcEndAt && t.EndAt > utcStartAt)
                .AnyAsync(cancellationToken);

            if (hasOverlap)
            {
                return Conflict(new { message = "Laiko langas u?imtas (persidengia u?duotys).", version = currentVersion });
            }

            var updated = await _planService.UpdateTaskAsync(id, task =>
            {
                task.Title = dto.Title;
                task.Location = normalizedLocation;
                task.StartAt = utcStartAt;
                task.EndAt = utcEndAt;
                task.Priority = dto.Priority;
                task.Status = dto.Status;
                task.SkillLevel = normalizedSkillLevel;
                task.Instructions = normalizedInstructions;
                if (dto.TechnicianComment != null)
                {
                    task.TechnicianComment = dto.TechnicianComment;
                }
            }, dto.TechnicianId, user, dto.TimezoneOffsetMinutes, normalizedObjectName, normalizedObjectAddress, cancellationToken);

            var version = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);

            var response = new TaskCreatedOrUpdatedResponseDto
            {
                Id = updated.Id,
                Version = version,
                Load = await BuildLoadSnapshotAsync(updated.TechnicianId, updated.StartAt.Date, cancellationToken)
            };

            _logger.LogInformation("api.task.update id={Id} planId={PlanId} version={Version}", id, updated.PlanId, version);
            return Ok(response);
        }

        [HttpPatch("{id:long}/move")]
        [Authorize(Policy = "CanEditTask")]
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

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.MoveTaskAsync(
                id,
                dto.TargetTechnicianId,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.Version,
                user,
                dto.TimezoneOffsetMinutes,
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
                    EndAt = localEndAt,
                    Load = await BuildLoadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
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

        [HttpPatch("{id:long}/technician-update")]
        [Authorize] // Allow any authenticated user (not just managers)
        public async Task<IActionResult> TechnicianUpdateTask(long id, [FromBody] TechnicianUpdateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var user = _userContextResolver.GetCurrentUser();
            if (user.IsManager)
            {
                return StatusCode(403, new { message = "Managers must use the main edit endpoint." });
            }

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (task == null)
            {
                return NotFound(new { message = "Uzduotis nerasta." });
            }

            if (user.IsTechnician && (!user.TechnicianId.HasValue || task.TechnicianId != user.TechnicianId.Value))
            {
                _logger.LogWarning(
                    "api.task.technician-update.forbidden userId={UserId} taskId={TaskId} ownerId={OwnerId}",
                    user.UserId, id, task.TechnicianId);
                return StatusCode(403, new { message = "Galite redaguoti tik savo uzduotis." });
            }

            if (!_timeService.IsTaskStillEditable(task.EndAt))
            {
                return UnprocessableEntity(new { message = "Si uzduotis nebegali buti redaguojama." });
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, dto.TimezoneOffsetMinutes);
            if (!canEdit)
            {
                _logger.LogWarning(
                    "api.task.technician-update.not-editable userId={UserId} taskId={TaskId}",
                    user.UserId, id);
                return StatusCode(403, new { message = "Si uzduotis nebegali buti redaguojama." });
            }

            var currentVersion = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);

            if (currentVersion != dto.Version)
            {
                _logger.LogWarning(
                    "api.task.technician-update.version-conflict taskId={Id} expected={Expected} actual={Actual}",
                    id, dto.Version, currentVersion);
                return Conflict(new
                {
                    message = "Uzduotis buvo pakeista kito vartotojo. Prasome atnaujinti puslapi.",
                    version = currentVersion
                });
            }

            task.Status = dto.Status;
            task.TechnicianComment = dto.Comment;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);

                var newVersion = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.Id == id)
                    .Select(t => EF.Property<uint>(t, "xmin"))
                    .FirstAsync(cancellationToken);

                _logger.LogInformation(
                    "api.task.technician-update.success userId={UserId} taskId={TaskId} status={Status}",
                    user.UserId, id, dto.Status);

                return Ok(new TaskCreatedOrUpdatedResponseDto
                {
                    Id = id,
                    Version = newVersion
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning(
                    "api.task.technician-update.concurrency taskId={Id}",
                    id);
                return Conflict(new { message = "Uzduotis buvo pakeista kito vartotojo. Prasome atnaujinti puslapi." });
            }
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

        /// Extract browser timezone offset from request headers.
        private int GetBrowserTimezoneOffset()
        {
            // Try header first (for AJAX requests)
            if (Request.Headers.TryGetValue("X-Timezone-Offset", out var tzHeader) &&
                int.TryParse(tzHeader.ToString(), out var offset))
            {
                return offset;
            }

            // Fallback to cookie (for initial page loads)
            if (Request.Cookies.TryGetValue("timezone_offset", out var tzCookie) &&
                int.TryParse(tzCookie, out var cookieOffset))
            {
                return cookieOffset;
            }

            return 0;
        }
    }
}
