using Xunit;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using Google.Protobuf;
using AppPipe.Hosting;

namespace AppPipe.Hosting.Tests;

public class TelemetryTests
{
    [Fact]
    public void AddTrace_ShouldParseAndStoreTrace()
    {
        // Arrange
        var store = new InMemoryTelemetryStore();
        var request = new ExportTraceServiceRequest();
        
        var resourceSpan = new ResourceSpans();
        resourceSpan.Resource = new OpenTelemetry.Proto.Resource.V1.Resource();
        resourceSpan.Resource.Attributes.Add(new KeyValue 
        { 
            Key = "service.name", 
            Value = new AnyValue { StringValue = "TestService" } 
        });

        var scopeSpan = new ScopeSpans();
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
            Name = "TestSpan",
            StartTimeUnixNano = 1700000000000000000,
            EndTimeUnixNano = 1700000001000000000
        };
        span.Attributes.Add(new KeyValue 
        { 
            Key = "http.method", 
            Value = new AnyValue { StringValue = "GET" } 
        });
        span.Attributes.Add(new KeyValue 
        { 
            Key = "http.target", 
            Value = new AnyValue { StringValue = "/api/test" } 
        });

        scopeSpan.Spans.Add(span);
        resourceSpan.ScopeSpans.Add(scopeSpan);
        request.ResourceSpans.Add(resourceSpan);

        // Act
        store.AddTrace(request);

        // Assert
        Assert.Single(store.Traces);
        var traceId = span.TraceId.ToBase64();
        Assert.True(store.Traces.TryGetValue(traceId, out var parsedTrace));
        Assert.NotNull(parsedTrace);
        Assert.Equal(traceId, parsedTrace.TraceId);
        Assert.Single(parsedTrace.AllSpans);
        
        var parsedSpan = parsedTrace.AllSpans[0];
        Assert.Equal("TestSpan", parsedSpan.Name);
        Assert.Equal("TestService", parsedSpan.ServiceName);
        Assert.Equal("GET", parsedSpan.HttpMethod);
        Assert.Equal("/api/test", parsedSpan.UrlPath);
    }

    [Fact]
    public void AddLog_ShouldParseAndStoreLog()
    {
        // Arrange
        var store = new InMemoryTelemetryStore();
        var request = new ExportLogsServiceRequest();

        var resourceLog = new ResourceLogs();
        resourceLog.Resource = new OpenTelemetry.Proto.Resource.V1.Resource();
        resourceLog.Resource.Attributes.Add(new KeyValue
        {
            Key = "service.name",
            Value = new AnyValue { StringValue = "LogService" }
        });

        var scopeLog = new ScopeLogs();
        var logRecord = new LogRecord
        {
            TimeUnixNano = 1700000000000000000,
            SeverityText = "Info",
            Body = new AnyValue { StringValue = "Application Started" }
        };
        logRecord.Attributes.Add(new KeyValue
        {
            Key = "custom.key",
            Value = new AnyValue { StringValue = "custom.value" }
        });

        scopeLog.LogRecords.Add(logRecord);
        resourceLog.ScopeLogs.Add(scopeLog);
        request.ResourceLogs.Add(resourceLog);

        // Act
        store.AddLog(request);

        // Assert
        Assert.Single(store.Logs);
        Assert.True(store.Logs.TryPeek(out var parsedLog));
        Assert.NotNull(parsedLog);
        Assert.Equal("Info", parsedLog.Severity);
        Assert.Equal("Application Started", parsedLog.Message);
        Assert.Equal("LogService", parsedLog.ServiceName);
        Assert.Equal("custom.value", parsedLog.Attributes["custom.key"]);
    }

    [Fact]
    public void AddMetric_ShouldStoreMetric()
    {
        // Arrange
        var store = new InMemoryTelemetryStore();
        var request = new ExportMetricsServiceRequest();

        // Act
        store.AddMetric(request);

        // Assert
        Assert.Single(store.Metrics);
    }

    [Fact]
    public void TelemetryReceivedEvent_ShouldFireOnNewTelemetry()
    {
        // Arrange
        var store = new InMemoryTelemetryStore();
        var fired = false;
        store.OnTelemetryReceived += () => fired = true;

        var request = new ExportMetricsServiceRequest();

        // Act
        store.AddMetric(request);

        // Assert
        Assert.True(fired);
    }
}
