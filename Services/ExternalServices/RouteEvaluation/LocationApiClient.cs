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
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "route.evaluation.geocode.http-failed address={Address} status={StatusCode} reason={Reason} payload={PayloadPreview}",
                    address,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    ToPayloadPreview(payload));
                throw new HttpRequestException(
                    $"Geocode request failed with status code {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "route.evaluation.geocode.parse-failed address={Address} payload={PayloadPreview}",
                    address,
                    ToPayloadPreview(payload));
                throw;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
                {
                    _logger.LogWarning(
                        "route.evaluation.geocode.invalid-payload address={Address} reason=missing-features payload={PayloadPreview}",
                        address,
                        ToPayloadPreview(payload));
                    throw new InvalidOperationException($"No geocoding result for address '{address}'.");
                }

                var firstFeature = features[0];
                if (!firstFeature.TryGetProperty("geometry", out var geometry) ||
                    !geometry.TryGetProperty("coordinates", out var coords) ||
                    coords.GetArrayLength() < 2)
                {
                    _logger.LogWarning(
                        "route.evaluation.geocode.invalid-payload address={Address} reason=missing-coordinates payload={PayloadPreview}",
                        address,
                        ToPayloadPreview(payload));
                    throw new InvalidOperationException($"Invalid geocoding payload for address '{address}'.");
                }

                var lon = coords[0].GetDouble();
                var lat = coords[1].GetDouble();
                _logger.LogInformation("route.evaluation.geocode.success address={Address}", address);
                return new GeoPoint(
                    decimal.Parse(lat.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    decimal.Parse(lon.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            }
        }

        private static string NormalizeAddressKey(string address)
        {
            return string.Join(" ", address.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
