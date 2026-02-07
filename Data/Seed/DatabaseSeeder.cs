using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

        public DatabaseSeeder(ScheduleDbContext db, IScheduleTimeService timeService)
        {
            _db = db;
            _timeService = timeService;
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

            // Seed Objects
            var objectLookup = await SeedObjectsAsync(regionLookup, addressLookup);

            // Seed Plan and Tasks
            // Build address ID lookup for tasks
            var addressById = addressLookup.Values.ToDictionary(a => a.Id);
            await SeedPlanAndTasksAsync(technicianLookup, regionLookup, objectLookup, addressLookup, addressById);

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

                var address = await EnsureAddressAsync(mockTech.WorkplaceAddress, addressLookup);

                var technician = new TechnicianEntity
                {
                    UserId = user.Id,
                    RegionId = regionLookup.TryGetValue(mockTech.RegionName, out var region) ? region.Id : null,
                    WorkplaceAddressId = address.Id,
                    WorkplaceLatitude = mockTech.WorkplaceLatitude,
                    WorkplaceLongitude = mockTech.WorkplaceLongitude,
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

        private async Task<AddressEntity> EnsureAddressAsync(string fullAddress, Dictionary<string, AddressEntity> addressLookup)
        {
            if (addressLookup.TryGetValue(fullAddress, out var existing))
            {
                return existing;
            }

            var address = new AddressEntity
            {
                FullAddress = fullAddress,
                CreatedAt = DateTime.UtcNow
            };
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync();
            addressLookup[fullAddress] = address;
            return address;
        }

        private async Task<Dictionary<string, ObjectEntity>> SeedObjectsAsync(
            Dictionary<string, RegionEntity> regionLookup,
            Dictionary<string, AddressEntity> addressLookup)
        {
            var mockObjects = MockSeedData.GetObjects();
            var existingObjects = await _db.Objects.ToListAsync();
            var objectLookup = existingObjects.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var mockObject in mockObjects)
            {
                if (objectLookup.ContainsKey(mockObject.Name)) continue;

                var addr = await EnsureAddressAsync(mockObject.Address, addressLookup);

                var obj = new ObjectEntity
                {
                    Name = mockObject.Name,
                    Address = mockObject.Address,
                    AddressId = addr.Id,
                    RegionId = regionLookup.TryGetValue(mockObject.RegionName, out var region) ? region.Id : null,
                    Description = mockObject.Description,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Objects.Add(obj);
                objectLookup[mockObject.Name] = obj;
            }

            await _db.SaveChangesAsync();
            return objectLookup;
        }

        private async Task SeedPlanAndTasksAsync(
            Dictionary<string, TechnicianEntity> technicianLookup,
            Dictionary<string, RegionEntity> regionLookup,
            Dictionary<string, ObjectEntity> objectLookup,
            Dictionary<string, AddressEntity> addressLookupByFull,
            Dictionary<long, AddressEntity> addressById)
        {
            var weekStart = _timeService.GetWeekStart(DateTime.Today);
            var mockTasks = MockSeedData.GetTasks(weekStart, 5);

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

            // Lithuania timezone offset (UTC+2 in winter, UTC+3 in summer)
            var timezoneOffsetMinutes = 120; // EET (Eastern European Time) = UTC+2

            var taskEntities = new List<TaskEntity>();
            var evaluations = new List<TaskEvaluationEntity>();

            foreach (var mockTask in mockTasks)
            {
                if (!technicianLookup.TryGetValue(mockTask.TechnicianName, out var technician))
                {
                    continue; // Skip if technician not found
                }

                // Convert local times to UTC
                var startUtc = _timeService.ConvertLocalToUtc(mockTask.StartTime, timezoneOffsetMinutes);
                var endUtc = _timeService.ConvertLocalToUtc(mockTask.EndTime, timezoneOffsetMinutes);

                // Find object if specified
                long? objectId = null;
                if (!string.IsNullOrWhiteSpace(mockTask.ObjectName) &&
                    objectLookup.TryGetValue(mockTask.ObjectName, out var obj))
                {
                    objectId = obj.Id;
                }

                var taskEntity = new TaskEntity
                {
                    PlanId = activePlan.Id,
                    TechnicianId = technician.Id,
                    ObjectId = objectId,
                    Title = mockTask.Title,
                    Location = mockTask.Location,
                    RegionId = regionLookup.TryGetValue(mockTask.Location, out var region) ? region.Id : null,
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

            // Wire previous task chain + travel estimation for each technician per day (using address entities + travel routes).
            var travelCache = new Dictionary<(long fromId, long toId), TravelRouteEntity>();
            foreach (var techGroup in taskEntities.GroupBy(t => t.TechnicianId))
            {
                var techId = techGroup.Key;
                var technician = technicianLookup.Values.First(t => t.Id == techId);

                foreach (var dayGroup in techGroup.OrderBy(t => t.StartAt).GroupBy(t => t.StartAt.Date))
                {
                    TaskEntity? prev = null;
                    foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                    {
                        var fromAddr = prev != null
                            ? await GetTaskAddressEntityAsync(prev, addressLookupByFull, addressById, objectLookup)
                            : addressById[technician.WorkplaceAddressId];
                        var toAddr = await GetTaskAddressEntityAsync(task, addressLookupByFull, addressById, objectLookup);

                        var key = (fromAddr.Id, toAddr.Id);
                        if (!travelCache.TryGetValue(key, out var cached))
                        {
                            var (distanceKm, durationMinutes) = EstimateTravel(fromAddr.FullAddress, toAddr.FullAddress, technician.RegionId, task.RegionId);
                            cached = new TravelRouteEntity
                            {
                                FromAddressId = fromAddr.Id,
                                ToAddressId = toAddr.Id,
                                DistanceKm = distanceKm,
                                DurationMinutes = durationMinutes,
                                CalculatedAt = DateTime.UtcNow
                            };
                            travelCache[key] = cached;
                            _db.TravelRoutes.Add(cached);
                        }

                        task.TravelDistanceKm = cached.DistanceKm;
                        task.TravelDurationMinutes = cached.DurationMinutes;
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
        }

        private async Task<AddressEntity> GetTaskAddressEntityAsync(
            TaskEntity task,
            Dictionary<string, AddressEntity> addressLookupByFull,
            Dictionary<long, AddressEntity> addressById,
            Dictionary<string, ObjectEntity> objectLookup)
        {
            if (task.ObjectId.HasValue)
            {
                var obj = objectLookup.Values.FirstOrDefault(o => o.Id == task.ObjectId.Value);
                if (obj?.AddressId != null && addressById.TryGetValue(obj.AddressId.Value, out var addr))
                {
                    return addr;
                }
            }

            // Fallback: synthetic address per location string (ensure it exists in addressLookup)
            var fallback = $"Location: {task.Location}";
            if (addressLookupByFull.TryGetValue(fallback, out var existing))
            {
                return existing;
            }

            var newAddr = await EnsureAddressAsync(fallback, addressLookupByFull);
            addressById[newAddr.Id] = newAddr;
            return newAddr;
        }

        private static (decimal distanceKm, int durationMinutes) EstimateTravel(
            string fromAddress,
            string toAddress,
            long? fromRegionId,
            long? toRegionId)
        {
            // Simple deterministic mock: same region => short trip, different => longer.
            var sameRegion = fromRegionId.HasValue && toRegionId.HasValue && fromRegionId == toRegionId;
            var hash = Math.Abs((fromAddress + "|" + toAddress).GetHashCode());
            var baseKm = sameRegion ? 3 + (hash % 10) : 40 + (hash % 60); // 3-12km intra-region, 40-99km inter-region
            var distanceKm = (decimal)baseKm;
            var durationMinutes = Math.Max(10, (int)Math.Round(baseKm / 40.0 * 60)); // assume ~40km/h avg + clamp to 10 min
            return (distanceKm, durationMinutes);
        }
    }
}
