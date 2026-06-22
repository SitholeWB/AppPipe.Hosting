using Xunit;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using Google.Protobuf;
using AppPipe.Hosting;
using Microsoft.Extensions.Configuration;

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

    [Fact]
    public async Task SqliteStore_ShouldPersistAndPruneTelemetry()
    {
        // Arrange
        var dbFile = Path.Combine(AppContext.BaseDirectory, "test_telemetry.db");
        if (File.Exists(dbFile)) File.Delete(dbFile);

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Telemetry:DatabasePath", dbFile },
                { "Telemetry:MaxDbRecords", "3" }
            })
            .Build();

        // 1. Act: Write telemetry to first store
        {
            var store1 = new SqliteTelemetryStore(config);

            var logRequest = new ExportLogsServiceRequest();
            var resourceLog = new ResourceLogs();
            resourceLog.Resource = new OpenTelemetry.Proto.Resource.V1.Resource();
            resourceLog.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "TestLogger" } });
            var scopeLog = new ScopeLogs();
            scopeLog.LogRecords.Add(new LogRecord { TimeUnixNano = 1700000000000000000, SeverityText = "Info", Body = new AnyValue { StringValue = "Log 1" } });
            resourceLog.ScopeLogs.Add(scopeLog);
            logRequest.ResourceLogs.Add(resourceLog);

            store1.AddLog(logRequest);

            // Wait a moment for background task to complete
            await Task.Delay(200);

            Assert.Single(store1.Logs);
        }

        // 2. Hydration Act: Create a second store instance pointing to same file and check
        {
            var store2 = new SqliteTelemetryStore(config);
            Assert.Single(store2.Logs);
            Assert.True(store2.Logs.TryPeek(out var log));
            Assert.Equal("Log 1", log.Message);
            Assert.Equal("TestLogger", log.ServiceName);
        }

        // 3. Pruning Act: Insert multiple logs exceeding the max limit (3)
        {
            var store3 = new SqliteTelemetryStore(config);
            var logRequest = new ExportLogsServiceRequest();
            var resourceLog = new ResourceLogs();
            resourceLog.Resource = new OpenTelemetry.Proto.Resource.V1.Resource();
            resourceLog.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "TestLogger" } });
            var scopeLog = new ScopeLogs();

            for (int i = 2; i <= 6; i++)
            {
                scopeLog.LogRecords.Add(new LogRecord 
                { 
                    TimeUnixNano = (ulong)(1700000000000000000L + (i * 1000L)), 
                    SeverityText = "Info", 
                    Body = new AnyValue { StringValue = $"Log {i}" } 
                });
            }

            resourceLog.ScopeLogs.Add(scopeLog);
            logRequest.ResourceLogs.Add(resourceLog);

            store3.AddLog(logRequest);

            // Wait a moment for background task to commit and prune
            await Task.Delay(300);

            // Create a fourth store instance to check pruned database contents
            var store4 = new SqliteTelemetryStore(config);
            // The limit is 3, so store4 should have at most 3 items hydrated
            Assert.True(store4.Logs.Count <= 3, $"Log count is {store4.Logs.Count}, expected <= 3 due to pruning");
        }

        // Clean up
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(dbFile)) File.Delete(dbFile);
    }
}
