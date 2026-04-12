using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using ServiceTaskPlanningSystem.Services.ExternalServices.ExternalApis;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public interface ISnapApiClient
    {
        /// <summary>
        /// Snaps each coordinate to the nearest point on the driving-car road graph
        /// within <paramref name="radiusMeters"/>. Returns null per-slot where no road
        /// was found within the radius.
        /// Result array is index-aligned with <paramref name="coordinates"/>.
        /// </summary>
        Task<IReadOnlyList<GeoPoint?>> SnapAsync(
            IReadOnlyList<GeoPoint> coordinates,
            int radiusMeters = 500,
            CancellationToken cancellationToken = default);
    }

    public class SnapApiClient : ISnapApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly HeigitApiOptions _heigitOptions;
        private readonly IExternalCallPolicy _policy;
        private readonly IExternalCallKeyedLock _keyedLock;
        private readonly ILogger<SnapApiClient> _logger;

        public SnapApiClient(
            HttpClient httpClient,
            IConfiguration configuration,
            HeigitApiOptions heigitOptions,
            IExternalCallPolicy policy,
            IExternalCallKeyedLock keyedLock,
            ILogger<SnapApiClient> logger)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["HEIGIT_BASE_URL"] ?? "https://api.openrouteservice.org";
            _apiKey = configuration["HEIGIT_API_KEY"] ?? string.Empty;
            _heigitOptions = heigitOptions;
            _policy = policy;
            _keyedLock = keyedLock;
            _logger = logger;
        }

        public async Task<IReadOnlyList<GeoPoint?>> SnapAsync(
            IReadOnlyList<GeoPoint> coordinates,
            int radiusMeters = 500,
            CancellationToken cancellationToken = default)
        {
            if (coordinates.Count == 0)
            {
                return Array.Empty<GeoPoint?>();
            }

            var key = HeigitEndpoints.Snap + ":" + radiusMeters + ":" + string.Join(
                "|",
                coordinates.Select(c =>
                    $"{c.Latitude.ToString(CultureInfo.InvariantCulture)},{c.Longitude.ToString(CultureInfo.InvariantCulture)}"));

            return await _keyedLock.RunSingleFlightAsync(
                key,
                async _ => await _policy.ExecuteAsync(
                    HeigitEndpoints.Snap,
                    _heigitOptions.ResolvePerMinute(HeigitEndpoints.Snap),
                    ct => ExecuteSnapAsync(coordinates, radiusMeters, ct),
                    cancellationToken),
                cancellationToken);
        }

        private async Task<IReadOnlyList<GeoPoint?>> ExecuteSnapAsync(
            IReadOnlyList<GeoPoint> coordinates,
            int radiusMeters,
            CancellationToken cancellationToken)
        {
            var locations = string.Join(
                ",",
                coordinates.Select(c =>
                    $"[{c.Longitude.ToString(CultureInfo.InvariantCulture)},{c.Latitude.ToString(CultureInfo.InvariantCulture)}]"));

            var body = $"{{\"locations\":[{locations}],\"radius\":{radiusMeters}}}";

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl.TrimEnd('/')}/v2/snap/driving-car")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Authorization", _apiKey);

            _logger.LogDebug(
                "route.snap.request coordinateCount={CoordinateCount} body={Body}",
                coordinates.Count,
                body);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug(
                "route.snap.response status={StatusCode} payload={Payload}",
                (int)response.StatusCode,
                payload);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "route.snap.http-failed status={StatusCode} reason={Reason} coordinateCount={CoordinateCount} payload={PayloadPreview}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    coordinates.Count,
                    ToPayloadPreview(payload));
                throw new HttpRequestException(
                    $"Snap request failed with status code {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var result = ParseSnapLocations(doc.RootElement, coordinates.Count);

                var snappedCount = result.Count(r => r != null);
                _logger.LogInformation(
                    "route.snap.success coordinateCount={CoordinateCount} snapped={Snapped} fallback={Fallback}",
                    coordinates.Count,
                    snappedCount,
                    coordinates.Count - snappedCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "route.snap.parse-failed coordinateCount={CoordinateCount} payload={PayloadPreview}",
                    coordinates.Count,
                    ToPayloadPreview(payload));
                throw;
            }
        }

        private static IReadOnlyList<GeoPoint?> ParseSnapLocations(JsonElement root, int expectedCount)
        {
            var result = new GeoPoint?[expectedCount];

            if (!root.TryGetProperty("locations", out var locations))
            {
                return result;
            }

            var i = 0;
            foreach (var loc in locations.EnumerateArray())
            {
                if (i >= expectedCount) break;

                // ORS Snap V2 returns objects: {"location":[lon,lat],"name":"...","snapped_distance":N}
                // Null entries (no road found within radius) come through as JSON null.
                if (loc.ValueKind == JsonValueKind.Object &&
                    loc.TryGetProperty("location", out var coords) &&
                    coords.GetArrayLength() >= 2)
                {
                    var lon = coords[0].GetDouble();
                    var lat = coords[1].GetDouble();
                    result[i] = new GeoPoint(
                        decimal.Parse(lat.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                        decimal.Parse(lon.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                }
                // else: JSON null or unexpected shape — slot stays null (no road found within radius)

                i++;
            }

            return result;
        }

        private static string ToPayloadPreview(string payload)
        {
            const int maxLength = 1200;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "<empty>";
            }

            var normalized = payload.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength) + "...";
        }
    }
}
