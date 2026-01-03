using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ServiceTaskPlanningSystem.Configuration
{
    public static class ConfigurationLoader
    {
        public static IConfiguration Build(string contentRootPath, string? environmentName = null)
        {
            var envName = !string.IsNullOrWhiteSpace(environmentName)
                ? environmentName
                : Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environments.Production;

            var builder = new ConfigurationBuilder()
                .SetBasePath(contentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: false)
                .AddUserSecrets<Program>(optional: true);

            var dotEnvPath = FindDotEnv(contentRootPath);
            if (dotEnvPath != null)
            {
                var envValues = ParseDotEnv(dotEnvPath);
                builder.AddInMemoryCollection(envValues);
            }

            builder.AddEnvironmentVariables();

            return builder.Build();
        }

        public static string GetRequiredConnectionString(IConfiguration configuration, string name = "DefaultConnection")
        {
            var connectionString = configuration.GetConnectionString(name);
            if (string.IsNullOrWhiteSpace(connectionString) ||
                connectionString.Contains("Use env var", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Connection string '{name}' is not configured. Set ConnectionStrings__{name} in environment variables or .env.");
            }

            return connectionString;
        }

        private static string? FindDotEnv(string startDirectory)
        {
            var current = startDirectory;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var candidate = Path.Combine(current, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return null;
        }

        private static IDictionary<string, string?> ParseDotEnv(string path)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = trimmed[..separatorIndex].Trim();
                var value = trimmed[(separatorIndex + 1)..].Trim();

                if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                {
                    value = value[1..^1];
                }

                if (!result.ContainsKey(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }
    }
}
