using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    /// <summary>
    /// Seeds the database with mock data if no plans exist.
    /// This orchestrator uses MockSeedData to populate the database following DB context and relations.
    /// </summary>
    public class DatabaseSeeder
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ILogger<DatabaseSeeder> _logger;

        public DatabaseSeeder(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ILogger<DatabaseSeeder> logger)
        {
            _db = db;
            _timeService = timeService;
            _routeEvaluationService = routeEvaluationService;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            if (await _db.Plans.AnyAsync())
            {
                return; // already seeded
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Address cache to enforce deduplication
            var addressLookup = new Dictionary<string, AddressEntity>(StringComparer.OrdinalIgnoreCase);

            // Seed Users
            var userLookup = await SeedUsersAsync();

            // Seed Regions
            var regionLookup = await SeedRegionsAsync();

            // Seed Technicians
            var technicianLookup = await SeedTechniciansAsync(userLookup, regionLookup, addressLookup);

            // Seed Service Objects
            var serviceObjectLookup = await SeedServiceObjectsAsync(regionLookup, addressLookup);

            // Seed Plan and Tasks
            // Build address ID lookup for tasks
            var addressById = addressLookup.Values.ToDictionary(a => a.Id);
            await SeedPlanAndTasksAsync(technicianLookup, serviceObjectLookup, addressById);

            await transaction.CommitAsync();
        }

        private async Task<Dictionary<string, UserEntity>> SeedUsersAsync()
        {
            var mockUsers = MockSeedData.GetUsers();
            var existingUsers = await _db.Users.ToListAsync();
            var userLookup = existingUsers.ToDictionary(u => u.Email, StringComparer.OrdinalIgnoreCase);

            foreach (var mockUser in mockUsers)
            {
                if (userLookup.ContainsKey(mockUser.Email)) continue;

                var user = new UserEntity
                {
                    Name = mockUser.Name,
                    Email = mockUser.Email,
                    Role = mockUser.Role,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Users.Add(user);
                userLookup[mockUser.Email] = user;
            }

            await _db.SaveChangesAsync();
            return userLookup.ToDictionary(kv => kv.Value.Name, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, RegionEntity>> SeedRegionsAsync()
        {
            var mockRegions = MockSeedData.GetRegions();
            var existingRegions = await _db.Regions.ToListAsync();
            var regionLookup = existingRegions.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var mockRegion in mockRegions)
            {
                if (regionLookup.ContainsKey(mockRegion.Name)) continue;

                var region = new RegionEntity
                {
                    Name = mockRegion.Name,
                    Country = mockRegion.Country,
                    PriorityWeight = mockRegion.PriorityWeight,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Regions.Add(region);
                regionLookup[mockRegion.Name] = region;
            }

            await _db.SaveChangesAsync();
            return regionLookup;
        }

        private async Task<Dictionary<string, TechnicianEntity>> SeedTechniciansAsync(
            Dictionary<string, UserEntity> userLookup,
            Dictionary<string, RegionEntity> regionLookup,
            Dictionary<string, AddressEntity> addressLookup)
        {
            var mockTechnicians = MockSeedData.GetTechnicians();
            var existingTechs = await _db.Technicians.Include(t => t.User).ToListAsync();
            var technicianLookup = existingTechs
                .Where(t => t.User != null)
                .ToDictionary(t => t.User!.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var mockTech in mockTechnicians)
            {
                if (technicianLookup.ContainsKey(mockTech.Name)) continue;

                if (!userLookup.TryGetValue(mockTech.Name, out var user))
                {
                    continue; // Skip if user not found
                }

                if (!regionLookup.TryGetValue(mockTech.RegionName, out var region))
                {
                    continue;
                }

                var address = await EnsureAddressAsync(mockTech.WorkplaceAddress, region.Id, addressLookup);

                var technician = new TechnicianEntity
                {
                    UserId = user.Id,
                    RegionId = region.Id,
                    WorkplaceAddressId = address.Id,
                    DailyLimit = mockTech.DailyLimit,
                    WeeklyLimit = mockTech.WeeklyLimit,
                    TravelSwitchLimit = mockTech.TravelSwitchLimit,
                    RulesSummary = mockTech.RulesSummary,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Technicians.Add(technician);
                technicianLookup[mockTech.Name] = technician;
            }

            await _db.SaveChangesAsync();
            return technicianLookup;
        }

        private async Task<AddressEntity> EnsureAddressAsync(
            string fullAddress,
            long regionId,
            Dictionary<string, AddressEntity> addressLookup)
        {
            if (addressLookup.TryGetValue(fullAddress, out var existing))
            {
                return existing;
            }

            var street = ParseStreet(fullAddress);
            var address = new AddressEntity
            {
                RegionId = regionId,
                Street = street,
                FullAddress = fullAddress,
                CreatedAt = DateTime.UtcNow
            };
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync();
            addressLookup[fullAddress] = address;
            return address;
        }

        private async Task<Dictionary<string, ServiceObjectEntity>> SeedServiceObjectsAsync(
            Dictionary<string, RegionEntity> regionLookup,
            Dictionary<string, AddressEntity> addressLookup)
        {
            var mockObjects = MockSeedData.GetObjects();
            var existingObjects = await _db.ServiceObjects.ToListAsync();
            var objectLookup = existingObjects.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var mockObject in mockObjects)
            {
                if (objectLookup.ContainsKey(mockObject.Name)) continue;

                if (!regionLookup.TryGetValue(mockObject.RegionName, out var region))
                {
                    continue;
                }

                var addr = await EnsureAddressAsync(mockObject.Address, region.Id, addressLookup);

                var obj = new ServiceObjectEntity
                {
                    Name = mockObject.Name,
                    AddressId = addr.Id,
                    Description = mockObject.Description,
                    CreatedAt = DateTime.UtcNow
                };

                _db.ServiceObjects.Add(obj);
                objectLookup[mockObject.Name] = obj;
            }

            await _db.SaveChangesAsync();
            return objectLookup;
        }

        private async Task SeedPlanAndTasksAsync(
            Dictionary<string, TechnicianEntity> technicianLookup,
            Dictionary<string, ServiceObjectEntity> serviceObjectLookup,
            Dictionary<long, AddressEntity> addressById)
        {
            var today = _timeService.GetLocalToday();
            var startDate = today.DayOfWeek switch
            {
                DayOfWeek.Saturday => today.AddDays(2),
                DayOfWeek.Sunday => today.AddDays(1),
                _ => today
            };
            var mockTasks = MockSeedData.GetTasks(startDate, 5);

            if (mockTasks.Count == 0) return;

            var periodStart = mockTasks.Min(t => t.StartTime.Date);
            var periodEnd = mockTasks.Max(t => t.EndTime.Date);

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

            // Seed timezone offset in minutes (currently fixed to Lithuania winter time: UTC+2).
            // TODO: Consider sourcing this from configuration or adding DST-aware logic.
            const int timezoneOffsetMinutes = 120;

            var taskEntities = new List<TaskEntity>();
            var evaluations = new List<TaskEvaluationEntity>();

            foreach (var mockTask in mockTasks)
            {
                if (!technicianLookup.TryGetValue(mockTask.TechnicianName, out var technician))
                {
                    continue; // Skip if technician not found
                }

                // Convert local times to UTC using the shared time service.
                var startMinutes = (int)Math.Round(mockTask.StartTime.TimeOfDay.TotalMinutes);
                var endMinutes = (int)Math.Round(mockTask.EndTime.TimeOfDay.TotalMinutes);
                var startUtc = _timeService.CreateUtcFromDateAndMinutes(mockTask.StartTime.Date, startMinutes, timezoneOffsetMinutes);
                var endUtc = _timeService.CreateUtcFromDateAndMinutes(mockTask.EndTime.Date, endMinutes, timezoneOffsetMinutes);

                // Find object if specified
                if (!serviceObjectLookup.TryGetValue(mockTask.ObjectName, out var serviceObject))
                {
                    continue;
                }

                var taskRegionId = addressById.TryGetValue(serviceObject.AddressId, out var serviceAddress)
                    ? serviceAddress.RegionId
                    : technician.RegionId ?? 0;
                if (taskRegionId == 0)
                {
                    continue;
                }

                var taskEntity = new TaskEntity
                {
                    PlanId = activePlan.Id,
                    TechnicianId = technician.Id,
                    ServiceObjectId = serviceObject.Id,
                    Title = mockTask.Title,
                    RegionId = taskRegionId,
                    StartAt = startUtc,
                    EndAt = endUtc,
                    Priority = mockTask.Priority,
                    Status = mockTask.Status,
                    SkillLevel = mockTask.SkillLevel,
                    Instructions = mockTask.Instructions ?? string.Empty,
                    TechnicianComment = string.Empty,
                    CreatedAt = DateTime.UtcNow
                };

                taskEntities.Add(taskEntity);
            }

            _db.Tasks.AddRange(taskEntities);
            await _db.SaveChangesAsync();

            // Wire previous task chain for each technician per day.
            foreach (var techGroup in taskEntities.GroupBy(t => t.TechnicianId))
            {
                foreach (var dayGroup in techGroup.OrderBy(t => t.StartAt).GroupBy(t => t.StartAt.Date))
                {
                    TaskEntity? prev = null;
                    foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                    {
                        task.PreviousTaskId = prev?.Id;

                        prev = task;
                    }
                }
            }

            _db.Tasks.UpdateRange(taskEntities);
            await _db.SaveChangesAsync();

            // Create task evaluations
            foreach (var task in taskEntities)
            {
                var duration = (task.EndAt - task.StartAt).TotalHours;
                var isLoad = duration >= 6.5;
                var isConflict = duration > 8;

                evaluations.Add(new TaskEvaluationEntity
                {
                    TaskId = task.Id,
                    RiskLevel = isConflict ? RiskLevel.High : isLoad ? RiskLevel.Medium : RiskLevel.Low,
                    IsConflict = isConflict,
                    HasConflict = isConflict,
                    Status = isConflict ? LoadState.Conflict : isLoad ? LoadState.Load : LoadState.Normal,
                    IsOverloaded = isConflict || isLoad,
                    TravelRequired = false,
                    LoadHours = (decimal)duration,
                    EvaluatedAt = DateTime.UtcNow
                });
            }

            _db.TaskEvaluations.AddRange(evaluations);
            await _db.SaveChangesAsync();

            // Populate coordinates and travel routes through the same external route-evaluation flow used by runtime operations.
            var seededDays = taskEntities
                .Select(t => t.StartAt.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            foreach (var day in seededDays)
            {
                try
                {
                    await _routeEvaluationService.EvaluatePlanDayAsync(activePlan.Id, day);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "seed.route-evaluation.failed planId={PlanId} day={Day}",
                        activePlan.Id,
                        day);
                }
            }
        }

        private static string ParseStreet(string fullAddress)
        {
            if (string.IsNullOrWhiteSpace(fullAddress))
            {
                return string.Empty;
            }

            var firstPart = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(firstPart))
            {
                return fullAddress.Trim();
            }

            return firstPart.Trim();
        }

    }
}
