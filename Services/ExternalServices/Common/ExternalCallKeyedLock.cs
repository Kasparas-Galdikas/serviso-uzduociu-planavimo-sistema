using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.Common
{
    public interface IExternalCallKeyedLock
    {
        Task<T> RunSingleFlightAsync<T>(
            string key,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default);
    }

    public class ExternalCallKeyedLock : IExternalCallKeyedLock
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);

        public async Task<T> RunSingleFlightAsync<T>(
            string key,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            Lazy<Task<object?>>? created = null;
            var holder = _inFlight.GetOrAdd(
                key,
                _ =>
                {
                    created = new Lazy<Task<object?>>(
                        async () => await action(CancellationToken.None).ConfigureAwait(false),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    return created;
                });

            try
            {
                var result = await holder.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    if (default(T) is null)
                    {
                        return default!;
                    }

                    throw new InvalidOperationException($"Single-flight action for key '{key}' returned null for non-nullable result type.");
                }

                if (result is T typed)
                {
                    return typed;
                }

                throw new InvalidCastException($"Single-flight result type mismatch for key '{key}'. Expected '{typeof(T).Name}', got '{result.GetType().Name}'.");
            }
            finally
            {
                if (ReferenceEquals(holder, created))
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
        }
    }
}
