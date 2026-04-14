using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ServiceTaskPlanningSystem.Infrastructure.Logging
{
    /// <summary>
    /// In-memory trace log store for development troubleshooting. Not intended for production scale.
    /// </summary>
    public class TraceLogStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TraceLogEntry>> _logs = new();
        private readonly TraceFilterOptions _options;
        private const int MaxTraces = 100;

        public TraceLogStore(TraceFilterOptions options)
        {
            _options = options;
        }

        public void Add(TraceLogEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.TraceId))
            {
                return;
            }

            var queue = _logs.GetOrAdd(entry.TraceId, _ => new ConcurrentQueue<TraceLogEntry>());
            queue.Enqueue(entry);
            Trim(queue);
            TrimTraceCount();
        }

        public void Clear(string traceId)
        {
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                _logs.TryRemove(traceId, out _);
            }
        }

        public IReadOnlyCollection<TraceLogEntry> Get(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return new List<TraceLogEntry>();
            }

            if (_logs.TryGetValue(traceId, out var queue))
            {
                return queue.ToList();
            }

            return new List<TraceLogEntry>();
        }

        private void Trim(ConcurrentQueue<TraceLogEntry> queue)
        {
            while (queue.Count > _options.MaxEntriesPerTrace && queue.TryDequeue(out _))
            {
            }
        }

        private void TrimTraceCount()
        {
            if (_logs.Count <= MaxTraces)
            {
                return;
            }

            var excess = _logs.Count - MaxTraces;
            var oldestKeys = _logs.Keys.Take(excess).ToList();
            foreach (var key in oldestKeys)
            {
                _logs.TryRemove(key, out _);
            }
        }
    }
}
