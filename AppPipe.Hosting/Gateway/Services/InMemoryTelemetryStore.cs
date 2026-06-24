using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using System.Collections.Concurrent;

namespace AppPipe.Hosting;

public class InMemoryTelemetryStore : ITelemetryStore
{
    private const int MaxItems = 500;

    public ConcurrentDictionary<string, ParsedTrace> Traces { get; } = new();
    protected ConcurrentQueue<string> TraceIds { get; } = new();

    public ConcurrentQueue<ParsedLog> Logs { get; } = new();
    public ConcurrentQueue<ExportMetricsServiceRequest> Metrics { get; } = new();

    // Tracks last-seen timestamp per service for health indicators
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenByService = new();

    public virtual void AddTrace(ExportTraceServiceRequest request)
    {
        foreach (var resourceSpan in request.ResourceSpans)
        {
            var serviceName = resourceSpan.Resource.Attributes
                .FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue ?? "Unknown Service";

            _lastSeenByService[serviceName] = DateTimeOffset.UtcNow;

            foreach (var scopeSpan in resourceSpan.ScopeSpans)
            {
                foreach (var span in scopeSpan.Spans)
                {
                    var traceId = span.TraceId.ToBase64();
                    var spanId = span.SpanId.ToBase64();
                    var parentSpanId = span.ParentSpanId.IsEmpty ? "" : span.ParentSpanId.ToBase64();

                    var startTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(span.StartTimeUnixNano / 1_000_000));
                    var endTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(span.EndTimeUnixNano / 1_000_000));

                    var httpMethod = span.Attributes.FirstOrDefault(a => a.Key == "http.method" || a.Key == "http.request.method")?.Value.StringValue;
                    var urlPath = span.Attributes.FirstOrDefault(a => a.Key == "http.target" || a.Key == "url.path")?.Value.StringValue;

                    var statusCodeAttr = span.Attributes.FirstOrDefault(a => a.Key == "http.status_code" || a.Key == "http.response.status_code");
                    int? statusCode = statusCodeAttr != null ? (int)statusCodeAttr.Value.IntValue : null;

                    var isError = span.Status?.Code == OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error || (statusCode >= 400);

                    var parsedSpan = new ParsedSpan(
                        traceId,
                        spanId,
                        parentSpanId,
                        span.Name,
                        startTime,
                        endTime,
                        endTime - startTime,
                        serviceName,
                        statusCode,
                        httpMethod,
                        urlPath,
                        isError
                    );

                    Traces.AddOrUpdate(traceId,
                        (id) =>
                        {
                            TraceIds.Enqueue(id);
                            var trace = new ParsedTrace(id, startTime, TimeSpan.Zero, null, new List<ParsedSpan> { parsedSpan }, isError);
                            return RecomputeTrace(trace);
                        },
                        (id, existingTrace) =>
                        {
                            var updatedSpans = existingTrace.AllSpans.ToList();
                            updatedSpans.Add(parsedSpan);
                            var trace = existingTrace with
                            {
                                AllSpans = updatedSpans,
                                HasError = existingTrace.HasError || isError,
                                StartTime = startTime < existingTrace.StartTime ? startTime : existingTrace.StartTime
                            };
                            return RecomputeTrace(trace);
                        });
                }
            }
        }

        while (TraceIds.Count > MaxItems && TraceIds.TryDequeue(out var oldId))
        {
            Traces.TryRemove(oldId, out _);
        }

        OnTelemetryReceived?.Invoke();
    }

    protected ParsedTrace RecomputeTrace(ParsedTrace trace)
    {
        if (trace.AllSpans.Count == 0) return trace;

        var spanDict = trace.AllSpans.ToDictionary(s => s.SpanId, s => s with { Children = new List<ParsedSpan>() });

        foreach (var span in spanDict.Values)
        {
            if (!string.IsNullOrEmpty(span.ParentSpanId) && spanDict.TryGetValue(span.ParentSpanId, out var parent))
            {
                parent.Children.Add(span);
            }
        }

        var allNewSpans = spanDict.Values.ToList();
        var root = allNewSpans.FirstOrDefault(s => string.IsNullOrEmpty(s.ParentSpanId) || !spanDict.ContainsKey(s.ParentSpanId));

        var minTime = allNewSpans.Min(s => s.StartTime);
        var maxTime = allNewSpans.Max(s => s.EndTime);

        return trace with { RootSpan = root ?? allNewSpans.First(), AllSpans = allNewSpans, StartTime = minTime, TotalDuration = maxTime - minTime };
    }

    public virtual void AddLog(ExportLogsServiceRequest request)
    {
        foreach (var resourceLog in request.ResourceLogs)
        {
            var serviceName = resourceLog.Resource.Attributes
                .FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue ?? "Unknown Service";

            _lastSeenByService[serviceName] = DateTimeOffset.UtcNow;

            foreach (var scopeLog in resourceLog.ScopeLogs)
            {
                foreach (var logRecord in scopeLog.LogRecords)
                {
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(logRecord.TimeUnixNano / 1_000_000));
                    var traceId = logRecord.TraceId.IsEmpty ? null : logRecord.TraceId.ToBase64();
                    var spanId = logRecord.SpanId.IsEmpty ? null : logRecord.SpanId.ToBase64();

                    var attributes = new Dictionary<string, string>();
                    foreach (var attr in logRecord.Attributes)
                    {
                        var val = attr.Value.ValueCase switch
                        {
                            OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.StringValue => attr.Value.StringValue,
                            OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.IntValue => attr.Value.IntValue.ToString(),
                            OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.DoubleValue => attr.Value.DoubleValue.ToString(),
                            OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.BoolValue => attr.Value.BoolValue.ToString(),
                            _ => attr.Value.ToString()
                        };
                        attributes[attr.Key] = val;
                    }

                    Logs.Enqueue(new ParsedLog(
                        timestamp,
                        logRecord.SeverityText,
                        logRecord.Body?.StringValue ?? "",
                        serviceName,
                        traceId,
                        spanId,
                        attributes
                    ));
                }
            }
        }

        while (Logs.Count > MaxItems) Logs.TryDequeue(out _);

        OnTelemetryReceived?.Invoke();
    }

    public virtual void AddMetric(ExportMetricsServiceRequest metric)
    {
        // Track service names from metrics resource attributes
        foreach (var rm in metric.ResourceMetrics)
        {
            var svc = rm.Resource?.Attributes.FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue;
            if (!string.IsNullOrEmpty(svc))
                _lastSeenByService[svc] = DateTimeOffset.UtcNow;
        }

        Metrics.Enqueue(metric);
        if (Metrics.Count > MaxItems) Metrics.TryDequeue(out _);
        OnTelemetryReceived?.Invoke();
    }

    public virtual void ClearLogs()
    {
        while (Logs.TryDequeue(out _)) { }
        OnTelemetryReceived?.Invoke();
    }

    public virtual void ClearTraces()
    {
        Traces.Clear();
        while (TraceIds.TryDequeue(out _)) { }
        OnTelemetryReceived?.Invoke();
    }

    public virtual void ClearMetrics()
    {
        while (Metrics.TryDequeue(out _)) { }
        OnTelemetryReceived?.Invoke();
    }

    public IReadOnlyList<string> GetServiceNames()
    {
        var names = new HashSet<string>();

        foreach (var log in Logs)
            names.Add(log.ServiceName);

        foreach (var trace in Traces.Values)
        {
            foreach (var span in trace.AllSpans)
                names.Add(span.ServiceName);
        }

        foreach (var key in _lastSeenByService.Keys)
            names.Add(key);

        return names.OrderBy(n => n).ToList();
    }

    /// <summary>Returns the last telemetry timestamp per service, for health indicators.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastSeenByService()
        => _lastSeenByService;

    public event Action? OnTelemetryReceived;
}