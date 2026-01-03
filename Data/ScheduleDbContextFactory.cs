using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ServiceTaskPlanningSystem.Data
{
    /// <summary>
    /// Design-time factory to enable EF CLI commands without manual env exports.
    /// Reads ConnectionStrings__DefaultConnection from environment or a local .env file.
    /// Throws if not found to avoid accidental placeholder usage.
    /// </summary>
    public class ScheduleDbContextFactory : IDesignTimeDbContextFactory<ScheduleDbContext>
    {
        public ScheduleDbContext CreateDbContext(string[] args)
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                                   ?? TryReadFromDotEnv();

            if (string.IsNullOrWhiteSpace(connectionString) ||
                connectionString.Contains("Use env var", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("DefaultConnection is not configured. Set ConnectionStrings__DefaultConnection in environment or .env file.");
            }

            var optionsBuilder = new DbContextOptionsBuilder<ScheduleDbContext>();
            optionsBuilder.UseNpgsql(connectionString)
                          .UseSnakeCaseNamingConvention();
            return new ScheduleDbContext(optionsBuilder.Options);
        }

        private static string? TryReadFromDotEnv()
        {
            var basePath = AppContext.BaseDirectory;
            string? foundPath = null;
            while (!string.IsNullOrWhiteSpace(basePath))
            {
                var candidate = Path.Combine(basePath, ".env");
                if (File.Exists(candidate))
                {
                    foundPath = candidate;
                    break;
                }
                basePath = Directory.GetParent(basePath)?.FullName;
            }

            Console.WriteLine(foundPath != null
                ? $"[DbContextFactory] .env found at: {foundPath}"
                : "[DbContextFactory] .env not found");

            if (foundPath == null)
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(foundPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                {
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0) continue;

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (string.Equals(key, "ConnectionStrings__DefaultConnection", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[DbContextFactory] Connection string found in .env");
                    return value;
                }
            }

            Console.WriteLine("[DbContextFactory] ConnectionStrings__DefaultConnection not found in .env");
            return null;
        }
    }
}
