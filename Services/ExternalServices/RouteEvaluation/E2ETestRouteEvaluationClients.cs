using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public sealed class E2ETestLocationApiClient : ILocationApiClient
    {
        public Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address must be provided.", nameof(address));
            }

            var normalized = address.Trim().ToUpperInvariant();
            var hash = normalized.Aggregate(17, (current, ch) => unchecked(current * 31 + ch));
            var latitude = 54m + Math.Abs(hash % 5000) / 10000m;
            var longitude = 23m + Math.Abs((hash / 7) % 5000) / 10000m;

            return Task.FromResult(new GeoPoint(latitude, longitude));
        }
    }

    public sealed class E2ETestDistanceApiClient : IDistanceApiClient
    {
        public Task<MatrixResult> GetMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken = default)
        {
            if (coordinates.Count < 2)
            {
                return Task.FromResult(MatrixResult.Empty(coordinates.Count));
            }

            var durations = new List<IReadOnlyList<double>>(coordinates.Count);
            var distances = new List<IReadOnlyList<double>>(coordinates.Count);

            for (var fromIndex = 0; fromIndex < coordinates.Count; fromIndex++)
            {
                var durationRow = new List<double>(coordinates.Count);
                var distanceRow = new List<double>(coordinates.Count);

                for (var toIndex = 0; toIndex < coordinates.Count; toIndex++)
                {
                    if (fromIndex == toIndex)
                    {
                        durationRow.Add(0d);
                        distanceRow.Add(0d);
                        continue;
                    }

                    var from = coordinates[fromIndex];
                    var to = coordinates[toIndex];
                    var latitudeDelta = (double)Math.Abs(from.Latitude - to.Latitude);
                    var longitudeDelta = (double)Math.Abs(from.Longitude - to.Longitude);
                    var distanceKm = Math.Sqrt((latitudeDelta * latitudeDelta) + (longitudeDelta * longitudeDelta)) * 111d;
                    var distanceMeters = Math.Max(250d, Math.Round(distanceKm * 1000d, 2));
                    var durationSeconds = Math.Max(60d, Math.Round((distanceKm / 45d) * 3600d, 2));

                    distanceRow.Add(distanceMeters);
                    durationRow.Add(durationSeconds);
                }

                distances.Add(distanceRow);
                durations.Add(durationRow);
            }

            return Task.FromResult(new MatrixResult(durations, distances));
        }
    }
}