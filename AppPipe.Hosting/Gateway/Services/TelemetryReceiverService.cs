using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace AppPipe.Hosting;

public class TraceReceiverService : TraceService.TraceServiceBase
{
    private readonly ITelemetryStore _store;

    public TraceReceiverService(ITelemetryStore store)
    {
        _store = store;
    }

    public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
    {
        _store.AddTrace(request);
        return Task.FromResult(new ExportTraceServiceResponse());
    }
}

public class LogsReceiverService : LogsService.LogsServiceBase
{
    private readonly ITelemetryStore _store;

    public LogsReceiverService(ITelemetryStore store)
    {
        _store = store;
    }

    public override Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        _store.AddLog(request);
        return Task.FromResult(new ExportLogsServiceResponse());
    }
}

public class MetricsReceiverService : MetricsService.MetricsServiceBase
{
    private readonly ITelemetryStore _store;

    public MetricsReceiverService(ITelemetryStore store)
    {
        _store = store;
    }

    public override Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
    {
        _store.AddMetric(request);
        return Task.FromResult(new ExportMetricsServiceResponse());
    }
}