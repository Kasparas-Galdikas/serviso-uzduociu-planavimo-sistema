using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Middleware
{
    /// <summary>
    /// Propagates TraceId/SpanId from headers and creates a logging scope so logs can be correlated end-to-end.
    /// </summary>
    public class TraceMiddleware
    {
        private const string TraceHeader = "X-Trace-Id";
        private const string SpanHeader = "X-Span-Id";
        private readonly RequestDelegate _next;
        private readonly ILogger<TraceMiddleware> _logger;

        public TraceMiddleware(RequestDelegate next, ILogger<TraceMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var traceId = GetOrCreateId(ReadTrace(context));
            var spanId = GetOrCreateId(context.Request.Headers[SpanHeader]);

            context.TraceIdentifier = traceId;

            using (_logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["TraceId"] = traceId,
                       ["SpanId"] = spanId,
                       ["Path"] = context.Request.Path.ToString(),
                       ["Layer"] = "api"
                   }))
            {
                context.Response.Headers[TraceHeader] = traceId;
                context.Response.Headers[SpanHeader] = spanId;
                await _next(context);
            }
        }

        private static string GetOrCreateId(string? incoming) =>
            string.IsNullOrWhiteSpace(incoming)
                ? Guid.NewGuid().ToString("N")
                : incoming!;

        private static string? ReadTrace(HttpContext ctx)
        {
            if (ctx.Request.Headers.TryGetValue(TraceHeader, out var header) && !string.IsNullOrWhiteSpace(header))
            {
                return header!;
            }
            if (ctx.Request.Cookies.TryGetValue("schedule_trace", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            {
                return cookie;
            }
            return null;
        }
    }
}
