using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data.Seed
{
    /// <summary>
    /// Provides mock seed data for the database.
    /// The seeder orchestrator uses this data to populate the database following DB context and relations.
    /// </summary>
    internal static class MockSeedData
    {
        public class MockUser
        {
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public UserRole Role { get; set; }
        }

        public class MockRegion
        {
            public string Name { get; set; } = string.Empty;
            public string Country { get; set; } = "Lithuania";
            public int PriorityWeight { get; set; }
        }

        public class MockTechnician
        {
            public string Name { get; set; } = string.Empty;
            public string RegionName { get; set; } = string.Empty;
            public decimal DailyLimit { get; set; }
            public decimal WeeklyLimit { get; set; }
            public int TravelSwitchLimit { get; set; }
            public string? RulesSummary { get; set; }
            public string WorkplaceAddress { get; set; } = string.Empty;
        }

        public class MockObject
        {
            public string Name { get; set; } = string.Empty;
            public string Address { get; set; } = string.Empty;
            public string RegionName { get; set; } = string.Empty;
            public string? Description { get; set; }
        }

        public class MockTask
        {
            public string TechnicianName { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TaskPriority Priority { get; set; }
            public ScheduleTaskStatus Status { get; set; }
            public string SkillLevel { get; set; } = string.Empty;
            public string ObjectName { get; set; } = string.Empty;
            public string? Instructions { get; set; }
        }

        public static List<MockUser> GetUsers() => new()
        {
            new() { Name = "Manager Test", Email = "manager@test.com", Role = UserRole.Manager },
            new() { Name = "Austeja J.", Email = "austeja.j@test.com", Role = UserRole.Technician },
            new() { Name = "Dovydas K.", Email = "dovydas.k@test.com", Role = UserRole.Technician },
            new() { Name = "Gabija P.", Email = "gabija.p@test.com", Role = UserRole.Technician },
            new() { Name = "Lukas V.", Email = "lukas.v@test.com", Role = UserRole.Technician },
            new() { Name = "Monika S.", Email = "monika.s@test.com", Role = UserRole.Technician },
            new() { Name = "Rokas M.", Email = "rokas.m@test.com", Role = UserRole.Technician },
            new() { Name = "Greta B.", Email = "greta.b@test.com", Role = UserRole.Technician },
            new() { Name = "Mantas P.", Email = "mantas.p@test.com", Role = UserRole.Technician },
            new() { Name = "Ieva Z.", Email = "ieva.z@test.com", Role = UserRole.Technician },
            new() { Name = "Paulius T.", Email = "paulius.t@test.com", Role = UserRole.Technician },
            new() { Name = "Jurate R.", Email = "jurate.r@test.com", Role = UserRole.Technician },
            new() { Name = "Tomas G.", Email = "tomas.g@test.com", Role = UserRole.Technician },
            new() { Name = "Elze K.", Email = "elze.k@test.com", Role = UserRole.Technician },
            new() { Name = "Vaidotas L.", Email = "vaidotas.l@test.com", Role = UserRole.Technician },
            new() { Name = "Karolina N.", Email = "karolina.n@test.com", Role = UserRole.Technician },
            new() { Name = "Technician", Email = "technician@test.com", Role = UserRole.Technician }
        };

        public static List<MockRegion> GetRegions() => new()
        {
            new() { Name = "Vilnius", Country = "Lithuania", PriorityWeight = 0 },
            new() { Name = "Kaunas", Country = "Lithuania", PriorityWeight = 0 },
            new() { Name = "Klaipeda", Country = "Lithuania", PriorityWeight = 0 },
            new() { Name = "Siauliai", Country = "Lithuania", PriorityWeight = 0 },
            new() { Name = "Panevezys", Country = "Lithuania", PriorityWeight = 0 },
            new() { Name = "Alytus", Country = "Lithuania", PriorityWeight = 0 }
        };

        public static List<MockTechnician> GetTechnicians() => new()
        {
            new() { Name = "Austeja J.", RegionName = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 2, RulesSummary = "Dienos limitas 8h, savaites 40h", WorkplaceAddress = "Gedimino pr. 1, Vilnius" },
            new() { Name = "Dovydas K.", RegionName = "Kaunas", DailyLimit = 8, WeeklyLimit = 45, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h, savaites 45h", WorkplaceAddress = "Laisvės al. 10, Kaunas" },
            new() { Name = "Gabija P.", RegionName = "Siauliai", DailyLimit = 8, WeeklyLimit = 42, TravelSwitchLimit = 3, RulesSummary = "Dienos limitas 8h, savaites 42h", WorkplaceAddress = "Vilniaus g. 100, Šiauliai" },
            new() { Name = "Lukas V.", RegionName = "Klaipeda", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "H. Manto g. 5, Klaipėda" },
            new() { Name = "Monika S.", RegionName = "Panevezys", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 0, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Respublikos g. 2, Panevėžys" },
            new() { Name = "Rokas M.", RegionName = "Vilnius", DailyLimit = 8, WeeklyLimit = 35, TravelSwitchLimit = 2, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Ozo g. 25, Vilnius" },
            new() { Name = "Greta B.", RegionName = "Kaunas", DailyLimit = 8, WeeklyLimit = 45, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Savanorių pr. 255, Kaunas" },
            new() { Name = "Mantas P.", RegionName = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 0, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Ukmergės g. 369, Vilnius" },
            new() { Name = "Ieva Z.", RegionName = "Alytus", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Naujoji g. 50, Alytus" },
            new() { Name = "Paulius T.", RegionName = "Klaipeda", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 2, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Taikos pr. 141, Klaipėda" },
            new() { Name = "Jurate R.", RegionName = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Pilies g. 10, Vilnius" },
            new() { Name = "Tomas G.", RegionName = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 3, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "K. Donelaičio g. 50, Kaunas" },
            new() { Name = "Elze K.", RegionName = "Siauliai", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Tilžės g. 50, Šiauliai" },
            new() { Name = "Vaidotas L.", RegionName = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 2, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Žalgirio g. 90, Vilnius" },
            new() { Name = "Karolina N.", RegionName = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Kęstučio g. 15, Kaunas" },
            new() { Name = "Technician", RegionName = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, TravelSwitchLimit = 1, RulesSummary = "Dienos limitas 8h", WorkplaceAddress = "Vytauto pr. 24, Kaunas" }
        };

        public static List<MockObject> GetObjects() => new()
        {
            new() { Name = "Vilniaus prekybos centras", Address = "Konstitucijos pr. 16, Vilnius", RegionName = "Vilnius", Description = "Didelis prekybos centras miesto centre" },
            new() { Name = "Kauno verslo kompleksas", Address = "Savanorių pr. 192, Kaunas", RegionName = "Kaunas", Description = "Biurų kompleksas" },
            new() { Name = "Klaipėdos uostas", Address = "Perkėlos g. 10, Klaipėda", RegionName = "Klaipeda", Description = "Uosto teritorija" },
            new() { Name = "Šiaulių ligoninė", Address = "Vytauto g. 23, Šiauliai", RegionName = "Siauliai", Description = "Rajono ligoninė" },
            new() { Name = "Panevėžio gamykla", Address = "Pramonės g. 15, Panevėžys", RegionName = "Panevezys", Description = "Gamybos įmonė" },
            new() { Name = "Alytaus mokykla", Address = "Naujoji g. 8, Alytus", RegionName = "Alytus", Description = "Vidurinė mokykla" },
            new() { Name = "Vilniaus oro uostas", Address = "Rodūnios kelias 10A, Vilnius", RegionName = "Vilnius", Description = "Tarptautinis oro uostas" },
            new() { Name = "Kauno technologijos universitetas", Address = "K. Donelaičio g. 73, Kaunas", RegionName = "Kaunas", Description = "Universiteto pastatai" },
            new() { Name = "Klaipėdos dramos teatras", Address = "Teatro a. 2, Klaipėda", RegionName = "Klaipeda", Description = "Miesto teatras" },
            new() { Name = "Vilniaus centrinė biblioteka", Address = "Žirmūnų g. 58, Vilnius", RegionName = "Vilnius", Description = "Viešoji biblioteka" }
        };

        public static List<MockTask> GetTasks(DateTime startDate, int totalDays)
        {
            var tasks = new List<MockTask>();
            var technicians = GetTechnicians();
            var objects = GetObjects();
            var durationOptions = new[] { 1.5, 2.0, 2.5, 3.0 };

            var generatedDays = 0;
            var currentDate = startDate.Date;
            while (generatedDays < totalDays)
            {
                if (currentDate.DayOfWeek == DayOfWeek.Saturday || currentDate.DayOfWeek == DayOfWeek.Sunday)
                {
                    currentDate = currentDate.AddDays(1);
                    continue;
                }

                for (var techIndex = 0; techIndex < technicians.Count; techIndex++)
                {
                    var technician = technicians[techIndex];
                    var startHour = 8.0 + (generatedDays % 3) * 0.5; // Vary start times slightly
                    var taskCount = 2 + ((techIndex + generatedDays) % 3);

                    for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
                    {
                        var duration = durationOptions[(techIndex + generatedDays + taskIndex) % durationOptions.Length];
                        var endHour = startHour + duration;

                        if (endHour > 18.0) break; // Don't schedule beyond 6 PM

                        var mockObject = objects[(techIndex + taskIndex) % objects.Count];

                        tasks.Add(new MockTask
                        {
                            TechnicianName = technician.Name,
                            Title = taskIndex == 0 ? "Ryto vizitas" : taskIndex == taskCount - 1 ? "Dienos pabaigos darbai" : $"Darbai - {mockObject.Name}",
                            StartTime = currentDate.AddHours(startHour),
                            EndTime = currentDate.AddHours(endHour),
                            Priority = TaskPriority.Medium,
                            Status = ScheduleTaskStatus.InProgress,
                            SkillLevel = "Basic",
                            ObjectName = mockObject.Name,
                            Instructions = $"Atlikti darbus objekte {mockObject.Name}"
                        });

                        startHour = endHour + 0.5; // 30-minute break between tasks
                    }
                }

                generatedDays++;
                currentDate = currentDate.AddDays(1);
            }

            return tasks;
        }
    }
}
