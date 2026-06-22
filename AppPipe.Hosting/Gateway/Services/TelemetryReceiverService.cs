using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace AppPipe.Hosting;

public class TraceReceiverService : TraceService.TraceServiceBase
{
    private readonly ITelemetryStore _store;
    private readonly GatewayDiagnosticsService _diagnostics;

    public TraceReceiverService(ITelemetryStore store, GatewayDiagnosticsService diagnostics)
    {
        _store = store;
        _diagnostics = diagnostics;
    }

    public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
    {
        int spanCount = 0;
        foreach (var rs in request.ResourceSpans)
        {
            foreach (var ss in rs.ScopeSpans)
            {
                spanCount += ss.Spans.Count;
            }
        }
        _diagnostics.AddSpans(spanCount);

        _store.AddTrace(request);
        return Task.FromResult(new ExportTraceServiceResponse());
    }
}

public class LogsReceiverService : LogsService.LogsServiceBase
{
    private readonly ITelemetryStore _store;
    private readonly GatewayDiagnosticsService _diagnostics;

    public LogsReceiverService(ITelemetryStore store, GatewayDiagnosticsService diagnostics)
    {
        _store = store;
        _diagnostics = diagnostics;
    }

    public override Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        int logCount = 0;
        foreach (var rl in request.ResourceLogs)
        {
            foreach (var sl in rl.ScopeLogs)
            {
                logCount += sl.LogRecords.Count;
            }
        }
        _diagnostics.AddLogs(logCount);

        _store.AddLog(request);
        return Task.FromResult(new ExportLogsServiceResponse());
    }
}

public class MetricsReceiverService : MetricsService.MetricsServiceBase
{
    private readonly ITelemetryStore _store;
    private readonly GatewayDiagnosticsService _diagnostics;

    public MetricsReceiverService(ITelemetryStore store, GatewayDiagnosticsService diagnostics)
    {
        _store = store;
        _diagnostics = diagnostics;
    }

    public override Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
    {
        int metricCount = 0;
        foreach (var rm in request.ResourceMetrics)
        {
            foreach (var sm in rm.ScopeMetrics)
            {
                metricCount += sm.Metrics.Count;
            }
        }
        _diagnostics.AddMetrics(metricCount);

        _store.AddMetric(request);
        return Task.FromResult(new ExportMetricsServiceResponse());
    }
}