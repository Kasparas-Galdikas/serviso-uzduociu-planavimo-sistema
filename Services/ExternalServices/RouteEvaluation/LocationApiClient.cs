using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using ServiceTaskPlanningSystem.Services.ExternalServices.ExternalApis;

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
        private readonly HeigitApiOptions _heigitOptions;
        private readonly IExternalCallPolicy _policy;
        private readonly IExternalCallKeyedLock _keyedLock;
        private readonly ILogger<LocationApiClient> _logger;

        public LocationApiClient(
            HttpClient httpClient,
            IConfiguration configuration,
            HeigitApiOptions heigitOptions,
            IExternalCallPolicy policy,
            IExternalCallKeyedLock keyedLock,
            ILogger<LocationApiClient> logger)
        {
            _httpClient = httpClient;
            _baseUrl = configuration["HEIGIT_BASE_URL"] ?? "https://api.openrouteservice.org";
            _apiKey = configuration["HEIGIT_API_KEY"] ?? string.Empty;
            _heigitOptions = heigitOptions;
            _policy = policy;
            _keyedLock = keyedLock;
            _logger = logger;
        }

        public async Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address must be provided.", nameof(address));
            }

            var key = $"{HeigitEndpoints.Geocode}:{NormalizeAddressKey(address)}";
            return await _keyedLock.RunSingleFlightAsync(
                key,
                async _ => await _policy.ExecuteAsync(
                    HeigitEndpoints.Geocode,
                    _heigitOptions.ResolvePerMinute(HeigitEndpoints.Geocode),
                    ct => ExecuteGeocodeAsync(address, ct),
                    cancellationToken),
                cancellationToken);
        }

        private async Task<GeoPoint> ExecuteGeocodeAsync(string address, CancellationToken cancellationToken)
        {
            var encodedAddress = Uri.EscapeDataString(address);
            var url = $"{_baseUrl.TrimEnd('/')}/geocode/search?text={encodedAddress}&size=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
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

            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            _logger.LogInformation("route.evaluation.geocode.success address={Address}", address);
            return new GeoPoint(
                decimal.Parse(lat.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                decimal.Parse(lon.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }

        private static string NormalizeAddressKey(string address)
        {
            return string.Join(" ", address.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
