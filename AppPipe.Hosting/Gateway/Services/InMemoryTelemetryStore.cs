using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using System.Collections.Concurrent;

namespace AppPipe.Hosting;

public class InMemoryTelemetryStore : ITelemetryStore
{
    private const int MaxItems = 200;

    public ConcurrentDictionary<string, ParsedTrace> Traces { get; } = new();
    protected ConcurrentQueue<string> TraceIds { get; } = new();

    public ConcurrentQueue<ParsedLog> Logs { get; } = new();
    public ConcurrentQueue<ExportMetricsServiceRequest> Metrics { get; } = new();

    public virtual void AddTrace(ExportTraceServiceRequest request)
    {
        bool newTraceAdded = false;

        foreach (var resourceSpan in request.ResourceSpans)
        {
            var serviceName = resourceSpan.Resource.Attributes
                .FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue ?? "Unknown Service";

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
                            newTraceAdded = true;
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
        Metrics.Enqueue(metric);
        if (Metrics.Count > MaxItems) Metrics.TryDequeue(out _);
        OnTelemetryReceived?.Invoke();
    }

    public event Action? OnTelemetryReceived;
}