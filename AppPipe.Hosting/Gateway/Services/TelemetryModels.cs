namespace AppPipe.Hosting;

public record ParsedSpan(
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string Name,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    TimeSpan Duration,
    string ServiceName,
    int? HttpStatusCode,
    string? HttpMethod,
    string? UrlPath,
    bool IsError)
{
    public List<ParsedSpan> Children { get; init; } = new();
}

public record ParsedTrace(
    string TraceId,
    DateTimeOffset StartTime,
    TimeSpan TotalDuration,
    ParsedSpan? RootSpan,
    List<ParsedSpan> AllSpans,
    bool HasError);

public record ParsedLog(
    DateTimeOffset Timestamp,
    string Severity,
    string Message,
    string ServiceName,
    string? TraceId,
    string? SpanId,
    Dictionary<string, string>? Attributes = null);