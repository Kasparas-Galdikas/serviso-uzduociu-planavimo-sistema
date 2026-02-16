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
    public interface IDistanceApiClient
    {
        Task<MatrixResult> GetMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken = default);
    }

    public class DistanceApiClient : IDistanceApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly HeigitApiOptions _heigitOptions;
        private readonly IExternalCallPolicy _policy;
        private readonly IExternalCallKeyedLock _keyedLock;
        private readonly ILogger<DistanceApiClient> _logger;

        public DistanceApiClient(
            HttpClient httpClient,
            IConfiguration configuration,
            HeigitApiOptions heigitOptions,
            IExternalCallPolicy policy,
            IExternalCallKeyedLock keyedLock,
            ILogger<DistanceApiClient> logger)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["HEIGIT_BASE_URL"] ?? "https://api.openrouteservice.org";
            _apiKey = configuration["HEIGIT_API_KEY"] ?? string.Empty;
            _heigitOptions = heigitOptions;
            _policy = policy;
            _keyedLock = keyedLock;
            _logger = logger;
        }

        public async Task<MatrixResult> GetMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken = default)
        {
            if (coordinates.Count < 2)
            {
                return MatrixResult.Empty(coordinates.Count);
            }

            var key = "matrix:" + string.Join(
                "|",
                coordinates.Select(c => $"{c.Latitude.ToString(CultureInfo.InvariantCulture)},{c.Longitude.ToString(CultureInfo.InvariantCulture)}"));

            return await _keyedLock.RunSingleFlightAsync(
                key,
                async _ => await _policy.ExecuteAsync(
                    HeigitEndpoints.Matrix,
                    _heigitOptions.ResolvePerMinute(HeigitEndpoints.Matrix),
                    ct => ExecuteMatrixAsync(coordinates, ct),
                    cancellationToken),
                cancellationToken);
        }

        private async Task<MatrixResult> ExecuteMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken)
        {
            var locations = coordinates
                .Select(c => new[] { c.Longitude.ToString(CultureInfo.InvariantCulture), c.Latitude.ToString(CultureInfo.InvariantCulture) })
                .ToArray();
            var body = "{\"locations\":[" + string.Join(",", locations.Select(l => $"[{l[0]},{l[1]}]")) + "],\"metrics\":[\"distance\",\"duration\"]}";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl.TrimEnd('/')}/v2/matrix/driving-car")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(payload);
            var distances = ParseMatrix(doc.RootElement.GetProperty("distances"));
            var durations = ParseMatrix(doc.RootElement.GetProperty("durations"));

            _logger.LogInformation("route.evaluation.matrix.success coordinateCount={CoordinateCount}", coordinates.Count);
            return new MatrixResult(durations, distances);
        }

        private static IReadOnlyList<IReadOnlyList<double>> ParseMatrix(JsonElement element)
        {
            return element.EnumerateArray()
                .Select(row => (IReadOnlyList<double>)row.EnumerateArray().Select(v => v.GetDouble()).ToList())
                .ToList();
        }
    }
}
