using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Services.Schedule;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    /// <summary>
    /// Seeds the database with the current mock data set if no plans exist.
    /// This keeps parity with the in-memory mock for local/demo runs.
    /// </summary>
    public class DatabaseSeeder
    {
        private readonly ScheduleDbContext _db;

        public DatabaseSeeder(ScheduleDbContext db)
        {
            _db = db;
        }

        public async Task SeedAsync()
        {
            if (await _db.Plans.AnyAsync())
            {
                return; // already seeded
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            var mockTechnicians = MockScheduleData.SeedTechnicians();

            // Regions (skip existing)
            var existingRegions = await _db.Regions.ToListAsync();
            var regionLookup = existingRegions.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            var regionNames = mockTechnicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l);

            var newRegions = new List<RegionEntity>();
            foreach (var name in regionNames)
            {
                if (regionLookup.ContainsKey(name)) continue;
                var region = new RegionEntity
                {
                    Name = name,
                    PriorityWeight = 0,
                    CreatedAt = DateTime.UtcNow
                };
                newRegions.Add(region);
                regionLookup[name] = region;
            }

            if (newRegions.Count > 0)
            {
                _db.Regions.AddRange(newRegions);
                await _db.SaveChangesAsync();
            }

            // Technicians (skip existing by name)
            var existingTechs = await _db.Technicians.ToListAsync();
            var technicianLookup = existingTechs.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var technicianEntities = new List<TechnicianEntity>();

            foreach (var t in mockTechnicians)
            {
                if (technicianLookup.ContainsKey(t.Name)) continue;

                var techEntity = new TechnicianEntity
                {
                    Name = t.Name,
                    RegionId = regionLookup.TryGetValue(t.Location, out var region) ? region.Id : null,
                    DailyLimit = t.DailyLimit > 0 ? t.DailyLimit : 8,
                    WeeklyLimit = t.WeeklyLimit > 0 ? t.WeeklyLimit : 40,
                    TravelSwitchLimit = t.TravelSwitches,
                    RulesSummary = t.RulesSummary,
                    CreatedAt = DateTime.UtcNow
                };
                technicianEntities.Add(techEntity);
                technicianLookup[t.Name] = techEntity;
            }

            if (technicianEntities.Count > 0)
            {
                _db.Technicians.AddRange(technicianEntities);
                await _db.SaveChangesAsync();
            }

            var weekStart = WeekCalculator.GetWeekStart(DateTime.Today);
            var baseTasks = MockScheduleData.SeedTasksRange(weekStart, 5, false);
            var periodStart = baseTasks.Min(t => t.Start.Date);
            var periodEnd = baseTasks.Max(t => t.End.Date);

            var activePlan = new PlanEntity
            {
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                Weeks = 1,
                Status = PlanStatus.Active,
                CreatedAt = DateTime.UtcNow
            };
            _db.Plans.Add(activePlan);
            await _db.SaveChangesAsync();
            var mockTechById = mockTechnicians.ToDictionary(t => t.Id);

            var taskEntities = new List<TaskEntity>();
            foreach (var task in baseTasks)
            {
                TechnicianEntity? technician = null;
                if (mockTechById.TryGetValue(task.TechnicianId, out var mockTech) &&
                    technicianLookup.TryGetValue(mockTech.Name, out var techEntity))
                {
                    technician = techEntity;
                }

                // IMPORTANT: Mock data generates LOCAL times (8:00, 10:30, etc.)
                // We need to convert them to UTC for storage.
                // Assuming Lithuania timezone (UTC+2 in winter, UTC+3 in summer)
                var timezoneOffsetMinutes = 120; // EET (Eastern European Time) = UTC+2

                DateTime startUtc, endUtc;

                if (task.Start.Kind == DateTimeKind.Utc)
                {
                    startUtc = task.Start;
                }
                else
                {
                    // Convert local time to UTC by subtracting timezone offset
                    var localStart = DateTime.SpecifyKind(task.Start, DateTimeKind.Unspecified);
                    startUtc = DateTime.SpecifyKind(localStart.AddMinutes(-timezoneOffsetMinutes), DateTimeKind.Utc);
                }

                if (task.End.Kind == DateTimeKind.Utc)
                {
                    endUtc = task.End;
                }
                else
                {
                    // Convert local time to UTC by subtracting timezone offset
                    var localEnd = DateTime.SpecifyKind(task.End, DateTimeKind.Unspecified);
                    endUtc = DateTime.SpecifyKind(localEnd.AddMinutes(-timezoneOffsetMinutes), DateTimeKind.Utc);
                }

                taskEntities.Add(new TaskEntity
                {
                    PlanId = activePlan.Id,
                    TechnicianId = technician?.Id ?? task.TechnicianId,
                    Title = task.Title,
                    Location = task.Location,
                    RegionId = regionLookup.TryGetValue(task.Location, out var r) ? r.Id : null,
                    StartAt = startUtc,
                    EndAt = endUtc,
                    Priority = MapPriority(task.Priority, task.Risk),
                    Status = ScheduleTaskStatus.Planned,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _db.Tasks.AddRange(taskEntities);
            await _db.SaveChangesAsync();

            var evaluations = taskEntities.Zip(baseTasks, (entity, mock) => new TaskEvaluationEntity
            {
                TaskId = entity.Id,
                RiskLevel = MapRiskLevel(mock.Risk),
                IsConflict = mock.IsConflict,
                HasConflict = mock.IsConflict,
                Status = mock.IsConflict ? LoadState.Conflict : mock.IsLoad ? LoadState.Load : LoadState.Normal,
                IsOverloaded = mock.IsConflict || mock.IsLoad,
                TravelRequired = mock.IsTravelSwitch,
                LoadHours = (decimal?)(mock.End - mock.Start).TotalHours,
                EvaluatedAt = DateTime.UtcNow
            }).ToList();

            _db.TaskEvaluations.AddRange(evaluations);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
        }

        private static TaskPriority MapPriority(string? priority, string? risk)
        {
            if (!string.IsNullOrWhiteSpace(priority))
            {
                return priority.ToLowerInvariant() switch
                {
                    "aukstas" => TaskPriority.High,
                    "high" => TaskPriority.High,
                    "zemas" => TaskPriority.Low,
                    "low" => TaskPriority.Low,
                    _ => TaskPriority.Medium
                };
            }

            return MapRiskLevel(risk) switch
            {
                RiskLevel.High => TaskPriority.High,
                RiskLevel.Low => TaskPriority.Low,
                _ => TaskPriority.Medium
            };
        }

        private static RiskLevel? MapRiskLevel(string? risk)
        {
            if (string.IsNullOrWhiteSpace(risk)) return null;
            return risk.ToLowerInvariant() switch
            {
                "aukstas" => RiskLevel.High,
                "high" => RiskLevel.High,
                "zemas" => RiskLevel.Low,
                "low" => RiskLevel.Low,
                _ => RiskLevel.Medium
            };
        }
    }
}
