using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using ServiceTaskPlanningSystem.Infrastructure.Logging;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("debug/trace")]
    public class DebugTraceController : ControllerBase
    {
        private readonly TraceLogStore _store;
        private readonly IHostEnvironment _env;

        public DebugTraceController(TraceLogStore store, IHostEnvironment env)
        {
            _store = store;
            _env = env;
        }

        [HttpGet("{traceId}")]
        public IActionResult Get(string traceId)
        {
            if (!_env.IsDevelopment())
            {
                return NotFound();
            }

            var entries = _store.Get(traceId)
                .OrderBy(e => e.TimestampUtc)
                .Select(e => new
                {
                    e.TimestampUtc,
                    e.TraceId,
                    e.SpanId,
                    Level = e.Level.ToString(),
                    e.Category,
                    EventId = e.EventId.Id,
                    e.Message,
                    e.Exception,
                    Scope = e.Scope
                });

            return Ok(entries);
        }
    }
}
