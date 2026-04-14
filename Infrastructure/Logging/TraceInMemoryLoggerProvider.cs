using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Infrastructure.Logging
{
    /// <summary>
    /// Captures scoped log entries into TraceLogStore when TraceId is present in the logging scope.
    /// </summary>
    public class TraceInMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly TraceLogStore _store;
        private readonly TraceFilterOptions _options;
        private IExternalScopeProvider? _scopeProvider;

        public TraceInMemoryLoggerProvider(TraceLogStore store, TraceFilterOptions options)
        {
            _store = store;
            _options = options;
        }

        public ILogger CreateLogger(string categoryName) => new TraceLogger(categoryName, _store, _options, _scopeProvider ?? new LoggerExternalScopeProvider());

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        private class TraceLogger : ILogger
        {
            private readonly string _category;
            private readonly TraceLogStore _store;
            private readonly TraceFilterOptions _options;
            private readonly IExternalScopeProvider _scopeProvider;

            public TraceLogger(string category, TraceLogStore store, TraceFilterOptions options, IExternalScopeProvider scopeProvider)
            {
                _category = category;
                _store = store;
                _options = options;
                _scopeProvider = scopeProvider;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var scope = new Dictionary<string, object?>();
                _scopeProvider.ForEachScope((scopeObj, _) =>
                {
                    if (scopeObj is IEnumerable<KeyValuePair<string, object?>> kvps)
                    {
                        foreach (var kvp in kvps)
                        {
                            scope[kvp.Key] = kvp.Value;
                        }
                    }
                }, state);

                if (!scope.TryGetValue("TraceId", out var traceObj) || traceObj is null)
                {
                    return;
                }

                var spanId = scope.TryGetValue("SpanId", out var spanObj) ? spanObj?.ToString() ?? string.Empty : string.Empty;

                // Filtering: skip EF command spam or duplicate messages per span.
                if (_options.SuppressEfCommands && _category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command"))
                {
                    return;
                }

                if (_options.SuppressTravelFeasibilityDetails && _category.EndsWith("TaskTravelFeasibilityService"))
                {
                    return;
                }

                var entry = new TraceLogEntry
                {
                    TraceId = traceObj.ToString() ?? string.Empty,
                    SpanId = spanId,
                    Level = logLevel,
                    Category = _category,
                    EventId = eventId,
                    Message = formatter(state, exception),
                    Exception = exception?.ToString() ?? string.Empty,
                    Scope = scope
                };

                // Optionally deduplicate by span or trace to reduce noise.
                if (_options.DeduplicatePerSpan && IsDuplicatePerSpan(spanId, entry))
                {
                    return;
                }

                _store.Add(entry);
            }

            private readonly ConcurrentDictionary<string, HashSet<string>> _spanMessages = new();
            private readonly ConcurrentDictionary<string, HashSet<string>> _traceMessages = new();

            private bool IsDuplicatePerSpan(string spanId, TraceLogEntry entry)
            {
                if (string.IsNullOrWhiteSpace(spanId))
                {
                    return false;
                }
                var set = _spanMessages.GetOrAdd(spanId, _ => new HashSet<string>(StringComparer.Ordinal));
                var key = $"{entry.Category}|{entry.Message}";
                if (set.Contains(key))
                {
                    return true;
                }
                set.Add(key);
                return false;
            }

            private bool IsDuplicatePerTrace(string traceId, TraceLogEntry entry)
            {
                if (string.IsNullOrWhiteSpace(traceId))
                {
                    return false;
                }
                var set = _traceMessages.GetOrAdd(traceId, _ => new HashSet<string>(StringComparer.Ordinal));
                var key = $"{entry.Category}|{entry.Message}";
                if (set.Contains(key))
                {
                    return true;
                }
                set.Add(key);
                return false;
            }
        }
    }
}
