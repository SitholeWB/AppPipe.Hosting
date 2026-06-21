using System;
using System.Collections.Concurrent;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace AppPipe.Gateway.Services;

public interface ITelemetryStore
{
    ConcurrentDictionary<string, ParsedTrace> Traces { get; }
    ConcurrentQueue<ParsedLog> Logs { get; }
    ConcurrentQueue<ExportMetricsServiceRequest> Metrics { get; }
    
    void AddTrace(ExportTraceServiceRequest request);
    void AddLog(ExportLogsServiceRequest request);
    void AddMetric(ExportMetricsServiceRequest metric);
    
    event Action? OnTelemetryReceived;
}
