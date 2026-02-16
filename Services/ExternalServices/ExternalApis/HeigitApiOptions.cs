using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.ExternalApis
{
    public static class HeigitEndpoints
    {
        public const string Geocode = "geocode";
        public const string Matrix = "matrix";
    }

    public sealed class HeigitEndpointRule
    {
        public int PerMinute { get; set; }
    }

    public sealed class HeigitApiOptions
    {
        public Dictionary<string, HeigitEndpointRule> Endpoints { get; } =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [HeigitEndpoints.Geocode] = new HeigitEndpointRule
                {
                    PerMinute = 80
                },
                [HeigitEndpoints.Matrix] = new HeigitEndpointRule
                {
                    PerMinute = 30
                }
            };

        public int ResolvePerMinute(string endpointKey)
        {
            if (Endpoints.TryGetValue(endpointKey, out var rule))
            {
                return rule.PerMinute;
            }

            throw new InvalidOperationException($"Heigit endpoint rule is not configured for key '{endpointKey}'.");
        }
    }
}
