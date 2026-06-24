using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using System.Collections.Concurrent;

namespace AppPipe.Hosting;

public interface ITelemetryStore
{
    ConcurrentDictionary<string, ParsedTrace> Traces { get; }
    ConcurrentQueue<ParsedLog> Logs { get; }
    ConcurrentQueue<ExportMetricsServiceRequest> Metrics { get; }

    void AddTrace(ExportTraceServiceRequest request);
    void AddLog(ExportLogsServiceRequest request);
    void AddMetric(ExportMetricsServiceRequest metric);

    void ClearLogs();
    void ClearTraces();
    void ClearMetrics();

    /// <summary>Returns distinct service names seen across all telemetry types.</summary>
    IReadOnlyList<string> GetServiceNames();

    event Action? OnTelemetryReceived;
}