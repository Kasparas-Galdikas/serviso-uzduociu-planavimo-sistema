using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Services.PlanManagement;

internal interface IPlanOperationInstrumentation
{
    PlanOperationScope BeginOperation(string operationName, IReadOnlyDictionary<string, object?> metadata);
}

internal sealed class PlanOperationInstrumentation : IPlanOperationInstrumentation
{
    private readonly ILogger<PlanOperationInstrumentation> _logger;

    public PlanOperationInstrumentation(ILogger<PlanOperationInstrumentation> logger)
    {
        _logger = logger;
    }

    public PlanOperationScope BeginOperation(string operationName, IReadOnlyDictionary<string, object?> metadata)
        => new(_logger, operationName, metadata);
}

internal sealed class PlanOperationScope : IDisposable, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly IDisposable? _scope;
    private readonly Dictionary<string, object?> _metadata;
    private readonly Dictionary<string, long> _phaseDurations = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    public PlanOperationScope(ILogger logger, string operationName, IReadOnlyDictionary<string, object?> metadata)
    {
        _logger = logger;
        OperationName = operationName;
        OperationId = Guid.NewGuid().ToString("N");
        _metadata = new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["operationName"] = operationName,
            ["operationId"] = OperationId
        };
        _scope = _logger.BeginScope(_metadata);
    }

    public string OperationName { get; }

    public string OperationId { get; }

    public PlanPhaseScope MeasurePhase(string phaseName, IReadOnlyDictionary<string, object?>? metadata = null)
        => new(this, phaseName, metadata);

    internal void RecordPhase(string phaseName, long durationMs, bool success, IReadOnlyDictionary<string, object?>? metadata)
    {
        _phaseDurations[phaseName] = durationMs;

        var itemCount = _metadata.TryGetValue("itemCount", out var count) ? count : null;
        var planMode = _metadata.TryGetValue("planMode", out var mode) ? mode : null;
        var phaseMetrics = metadata == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "{EventName} operationName={OperationName} operationId={OperationId} phase={Phase} durationMs={DurationMs} itemCount={ItemCount} planMode={PlanMode} success={Success} phaseMetrics={PhaseMetrics}",
            $"{OperationName}.phase",
            OperationName,
            OperationId,
            phaseName,
            durationMs,
            itemCount,
            planMode,
            success,
            phaseMetrics);
    }

    public Task CompleteAsync(bool success, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        Complete(success, metadata);
        return Task.CompletedTask;
    }

    public void Complete(bool success, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _stopwatch.Stop();

        var itemCount = _metadata.TryGetValue("itemCount", out var count) ? count : null;
        var planMode = _metadata.TryGetValue("planMode", out var mode) ? mode : null;
        var summaryMetrics = metadata == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
        var phaseDurations = _phaseDurations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "{EventName} operationName={OperationName} operationId={OperationId} totalDurationMs={TotalDurationMs} itemCount={ItemCount} planMode={PlanMode} success={Success} phaseDurations={PhaseDurations} summaryMetrics={SummaryMetrics}",
            $"{OperationName}.summary",
            OperationName,
            OperationId,
            _stopwatch.ElapsedMilliseconds,
            itemCount,
            planMode,
            success,
            phaseDurations,
            summaryMetrics);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Complete(success: false);
        _scope?.Dispose();
    }
}

internal sealed class PlanPhaseScope : IDisposable, IAsyncDisposable
{
    private readonly PlanOperationScope _operation;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly IReadOnlyDictionary<string, object?>? _metadata;
    private bool _success;
    private bool _disposed;

    public PlanPhaseScope(
        PlanOperationScope operation,
        string phaseName,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        _operation = operation;
        PhaseName = phaseName;
        _metadata = metadata;
    }

    public string PhaseName { get; }

    public void Complete(bool success = true)
    {
        _success = success;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();
        _operation.RecordPhase(PhaseName, _stopwatch.ElapsedMilliseconds, _success, _metadata);
    }
}
