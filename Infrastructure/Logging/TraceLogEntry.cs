using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Infrastructure.Logging
{
    public class TraceLogEntry
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public string TraceId { get; init; } = string.Empty;
        public string SpanId { get; init; } = string.Empty;
        public LogLevel Level { get; init; }
        public string Category { get; init; } = string.Empty;
        public EventId EventId { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? Exception { get; init; }
        public IDictionary<string, object?> Scope { get; init; } = new Dictionary<string, object?>();
    }
}
