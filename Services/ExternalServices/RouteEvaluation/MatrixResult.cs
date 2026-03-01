using System.Collections.Generic;
using System.Linq;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public sealed record GeoPoint(decimal Latitude, decimal Longitude);

    public sealed class MatrixResult
    {
        public MatrixResult(IReadOnlyList<IReadOnlyList<double>> durationsSeconds, IReadOnlyList<IReadOnlyList<double>> distancesMeters)
        {
            DurationsSeconds = durationsSeconds;
            DistancesMeters = distancesMeters;
        }

        public IReadOnlyList<IReadOnlyList<double>> DurationsSeconds { get; }
        public IReadOnlyList<IReadOnlyList<double>> DistancesMeters { get; }

        public static MatrixResult Empty(int size)
        {
            var durations = Enumerable.Range(0, size)
                .Select(_ => (IReadOnlyList<double>)Enumerable.Repeat(0d, size).ToList())
                .ToList();
            var distances = Enumerable.Range(0, size)
                .Select(_ => (IReadOnlyList<double>)Enumerable.Repeat(0d, size).ToList())
                .ToList();

            return new MatrixResult(durations, distances);
        }
    }
}
