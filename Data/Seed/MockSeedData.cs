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

        public class MockTechnician
        {
            public string Name { get; set; } = string.Empty;
            public string City { get; set; } = string.Empty;
            public decimal DailyLimit { get; set; }
            public decimal WeeklyLimit { get; set; }
            public string WorkplaceAddress { get; set; } = string.Empty;
        }

        public class MockObject
        {
            public string Name { get; set; } = string.Empty;
            public string Address { get; set; } = string.Empty;
            public string City { get; set; } = string.Empty;
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

        public static List<MockTechnician> GetTechnicians() => new()
        {
            new() { Name = "Austeja J.", City = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Gedimino pr. 1, Vilnius" },
            new() { Name = "Dovydas K.", City = "Kaunas", DailyLimit = 8, WeeklyLimit = 45, WorkplaceAddress = "Laisves al. 10, Kaunas" },
            new() { Name = "Gabija P.", City = "Siauliai", DailyLimit = 8, WeeklyLimit = 42, WorkplaceAddress = "Vilniaus g. 100, Siauliai" },
            new() { Name = "Lukas V.", City = "Klaipeda", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "H. Manto g. 5, Klaipeda" },
            new() { Name = "Monika S.", City = "Panevezys", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Respublikos g. 2, Panevezys" },
            new() { Name = "Rokas M.", City = "Vilnius", DailyLimit = 8, WeeklyLimit = 35, WorkplaceAddress = "Ozo g. 25, Vilnius" },
            new() { Name = "Greta B.", City = "Kaunas", DailyLimit = 8, WeeklyLimit = 45, WorkplaceAddress = "Savanoriu pr. 255, Kaunas" },
            new() { Name = "Mantas P.", City = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Ukmerges g. 369, Vilnius" },
            new() { Name = "Ieva Z.", City = "Alytus", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Naujoji g. 50, Alytus" },
            new() { Name = "Paulius T.", City = "Klaipeda", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Taikos pr. 141, Klaipeda" },
            new() { Name = "Jurate R.", City = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Pilies g. 10, Vilnius" },
            new() { Name = "Tomas G.", City = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "K. Donelaicio g. 50, Kaunas" },
            new() { Name = "Elze K.", City = "Siauliai", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Tilzes g. 50, Siauliai" },
            new() { Name = "Vaidotas L.", City = "Vilnius", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Zalgirio g. 90, Vilnius" },
            new() { Name = "Karolina N.", City = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Kestucio g. 15, Kaunas" },
            new() { Name = "Technician", City = "Kaunas", DailyLimit = 8, WeeklyLimit = 40, WorkplaceAddress = "Vytauto pr. 24, Kaunas" }
        };

        public static List<MockObject> GetObjects() => new()
        {
            new() { Name = "Vilniaus prekybos centras", Address = "Konstitucijos pr. 16, Vilnius", City = "Vilnius", Description = "Didelis prekybos centras miesto centre" },
            new() { Name = "Kauno verslo kompleksas", Address = "Savanoriu pr. 192, Kaunas", City = "Kaunas", Description = "Biuru kompleksas" },
            new() { Name = "Klaipedos uostas", Address = "Perkelos g. 10, Klaipeda", City = "Klaipeda", Description = "Uosto teritorija" },
            new() { Name = "Siauliu ligonine", Address = "Vytauto g. 23, Siauliai", City = "Siauliai", Description = "Rajono ligonine" },
            new() { Name = "Panevezio gamykla", Address = "Pramones g. 15, Panevezys", City = "Panevezys", Description = "Gamybos imone" },
            new() { Name = "Alytaus mokykla", Address = "Naujoji g. 8, Alytus", City = "Alytus", Description = "Vidurine mokykla" },
            new() { Name = "Vilniaus oro uostas", Address = "Rodunios kelias 10A, Vilnius", City = "Vilnius", Description = "Tarptautinis oro uostas" },
            new() { Name = "Kauno technologijos universitetas", Address = "K. Donelaicio g. 73, Kaunas", City = "Kaunas", Description = "Universiteto pastatai" },
            new() { Name = "Klaipedos dramos teatras", Address = "Teatro a. 2, Klaipeda", City = "Klaipeda", Description = "Miesto teatras" },
            new() { Name = "Vilniaus centrine biblioteka", Address = "Zirmunu g. 58, Vilnius", City = "Vilnius", Description = "Viesoji biblioteka" }
        };

        public static List<MockTask> GetTasks(DateTime startDate, int totalDays)
        {
            var tasks = new List<MockTask>();
            var technicians = GetTechnicians();
            var objects = GetObjects();
            var objectsByCity = objects
                .GroupBy(o => o.City, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var durationOptions = new[] { 1.5, 2.0, 2.5, 3.0 };
            var foreignAssignmentsRemaining = 2;

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
                    var startHour = 8.0 + (generatedDays % 3) * 0.5;
                    var taskCount = 2 + ((techIndex + generatedDays) % 3);

                    for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
                    {
                        var duration = durationOptions[(techIndex + generatedDays + taskIndex) % durationOptions.Length];
                        var endHour = startHour + duration;

                        if (endHour > 18.0) break;

                        var cityObjects = objectsByCity.TryGetValue(technician.City, out var matched)
                            ? matched
                            : objects;

                        var mockObject = cityObjects[(techIndex + generatedDays + taskIndex) % cityObjects.Count];

                        // Keep most seeded travel realistic (same-city),
                        // with only two controlled cross-city assignments.
                        if (foreignAssignmentsRemaining > 0 && generatedDays == 0 && taskIndex == 0)
                        {
                            var foreignCandidates = objects.Where(o =>
                                !string.Equals(o.City, technician.City, StringComparison.OrdinalIgnoreCase)).ToList();

                            if (foreignCandidates.Count > 0)
                            {
                                mockObject = foreignCandidates[(techIndex + generatedDays) % foreignCandidates.Count];
                                foreignAssignmentsRemaining--;
                            }
                        }

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

                        startHour = endHour + 0.5;
                    }
                }

                generatedDays++;
                currentDate = currentDate.AddDays(1);
            }

            return tasks;
        }
    }
}

