using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.Common
{
    public class ExternalCallPolicy : IExternalCallPolicy
    {
        private const int MaxRetries = 3;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan TooManyRequestsRetryDelay = TimeSpan.FromMilliseconds(2500);

        private readonly ILogger<ExternalCallPolicy> _logger;
        private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _limiters;

        public ExternalCallPolicy(ILogger<ExternalCallPolicy> logger)
        {
            _logger = logger;
            _limiters = new ConcurrentDictionary<string, TokenBucketRateLimiter>(StringComparer.OrdinalIgnoreCase);
        }

        public async Task<T> ExecuteAsync<T>(
            string endpointKey,
            int perMinuteLimit,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            if (perMinuteLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(perMinuteLimit), "Rate limit must be greater than zero.");
            }

            var limiterKey = $"{endpointKey}:{perMinuteLimit}";
            var limiter = ResolveLimiter(limiterKey, perMinuteLimit);
            using var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException($"Rate limiter rejected request for endpoint '{endpointKey}'.");
            }

            var attempts = 0;
            while (true)
            {
                attempts++;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(DefaultTimeout);

                try
                {
                    return await action(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRetryable(ex, out var delay))
                {
                    if (attempts >= MaxRetries)
                    {
                        throw;
                    }

                    _logger.LogWarning(
                        ex,
                        "external.call.retry endpoint={Endpoint} attempt={Attempt} delayMs={DelayMs}",
                        endpointKey,
                        attempts,
                        (int)delay.TotalMilliseconds);

                    var nextDelay = delay > DefaultRetryDelay ? delay : DefaultRetryDelay;
                    await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static TokenBucketRateLimiter CreateLimiter(int tokensPerMinute, int tokenLimit)
        {
            return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                QueueLimit = 500,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = tokensPerMinute,
                AutoReplenishment = true
            });
        }

        private TokenBucketRateLimiter ResolveLimiter(string limiterKey, int perMinuteLimit)
        {
            return _limiters.GetOrAdd(limiterKey, _ => CreateLimiter(perMinuteLimit, perMinuteLimit));
        }

        private static bool IsRetryable(Exception ex, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (ex is TaskCanceledException)
            {
                delay = TimeSpan.FromSeconds(1.5);
                return true;
            }

            if (ex is HttpRequestException hre)
            {
                var statusCode = hre.StatusCode;
                if (statusCode == HttpStatusCode.TooManyRequests)
                {
                    delay = TooManyRequestsRetryDelay;
                    return true;
                }

                if (statusCode == null || (int)statusCode >= 500)
                {
                    delay = DefaultRetryDelay;
                    return true;
                }
            }

            return false;
        }
    }
}
