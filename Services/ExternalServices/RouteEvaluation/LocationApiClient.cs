using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public interface ILocationApiClient
    {
        Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default);
    }

    public class LocationApiClient : ILocationApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly ILogger<LocationApiClient> _logger;

        public LocationApiClient(HttpClient httpClient, IConfiguration configuration, ILogger<LocationApiClient> logger)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["HEIGIT_BASE_URL"] ?? "https://api.openrouteservice.org";
            _apiKey = configuration["HEIGIT_API_KEY"] ?? string.Empty;
            _logger = logger;
        }

        public async Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address must be provided.", nameof(address));
            }

            var encodedAddress = Uri.EscapeDataString(address);
            var encodedApiKey = Uri.EscapeDataString(_apiKey);
            var url = $"{_baseUrl.TrimEnd('/')}/geocode/search?api_key={encodedApiKey}&text={encodedAddress}&size=1";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(payload);

            if (!doc.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"No geocoding result for address '{address}'.");
            }

            var firstFeature = features[0];
            if (!firstFeature.TryGetProperty("geometry", out var geometry) ||
                !geometry.TryGetProperty("coordinates", out var coords) ||
                coords.GetArrayLength() < 2)
            {
                throw new InvalidOperationException($"Invalid geocoding payload for address '{address}'.");
            }

            // OpenRouteService geocode returns [lon, lat].
            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            _logger.LogInformation("route.evaluation.geocode.success address={Address}", address);
            return new GeoPoint(
                decimal.Parse(lat.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                decimal.Parse(lon.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }
    }
}
