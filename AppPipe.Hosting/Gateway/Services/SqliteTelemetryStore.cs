using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Google.Protobuf;
using System.Text.Json;

namespace AppPipe.Hosting;

public class SqliteTelemetryStore : InMemoryTelemetryStore
{
    private readonly string _connectionString;
    private readonly int _maxDbRecords;
    private bool _dbEnabled = true;

    public string DatabasePath { get; }

    public SqliteTelemetryStore(IConfiguration config)
    {
        var dbPath = config.GetValue<string>("Telemetry:DatabasePath");
        if (string.IsNullOrEmpty(dbPath))
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, "telemetry.db");
        }

        dbPath = Path.GetFullPath(dbPath);
        DatabasePath = dbPath;
        _connectionString = $"Data Source={dbPath}";
        _maxDbRecords = config.GetValue<int>("Telemetry:MaxDbRecords", 2000);

        try
        {
            EnsureDatabaseCreated();
            LoadTelemetry();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SqliteTelemetryStore] SQLite initialization failed, falling back to memory only: {ex.Message}");
            _dbEnabled = false;
        }
    }

    public long GetDatabaseSize()
    {
        try
        {
            if (File.Exists(DatabasePath))
            {
                return new FileInfo(DatabasePath).Length;
            }
        }
        catch { }
        return 0;
    }

    private void EnsureDatabaseCreated()
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS Logs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Severity TEXT NOT NULL,
                Message TEXT NOT NULL,
                ServiceName TEXT NOT NULL,
                TraceId TEXT,
                SpanId TEXT,
                AttributesJson TEXT
            );

            CREATE TABLE IF NOT EXISTS Spans (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TraceId TEXT NOT NULL,
                SpanId TEXT NOT NULL,
                ParentSpanId TEXT,
                Name TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                DurationTicks INTEGER NOT NULL,
                ServiceName TEXT NOT NULL,
                HttpStatusCode INTEGER,
                HttpMethod TEXT,
                UrlPath TEXT,
                IsError INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Metrics (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MetricData BLOB NOT NULL,
                Timestamp TEXT NOT NULL
            );
        ", conn);
        cmd.ExecuteNonQuery();
    }

    private void LoadTelemetry()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 1. Load Logs
        using (var cmd = new SqliteCommand(@"
            SELECT Timestamp, Severity, Message, ServiceName, TraceId, SpanId, AttributesJson 
            FROM (SELECT * FROM Logs ORDER BY Id DESC LIMIT 200) 
            ORDER BY Id ASC", conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var timestamp = DateTimeOffset.Parse(reader.GetString(0));
                var severity = reader.GetString(1);
                var message = reader.GetString(2);
                var serviceName = reader.GetString(3);
                var traceId = reader.IsDBNull(4) ? null : reader.GetString(4);
                var spanId = reader.IsDBNull(5) ? null : reader.GetString(5);
                var attrJson = reader.IsDBNull(6) ? null : reader.GetString(6);

                Dictionary<string, string>? attributes = null;
                if (!string.IsNullOrEmpty(attrJson))
                {
                    try
                    {
                        attributes = JsonSerializer.Deserialize<Dictionary<string, string>>(attrJson);
                    }
                    catch { }
                }

                Logs.Enqueue(new ParsedLog(timestamp, severity, message, serviceName, traceId, spanId, attributes));
            }
        }

        // 2. Load Metrics
        using (var cmd = new SqliteCommand(@"
            SELECT MetricData 
            FROM (SELECT * FROM Metrics ORDER BY Id DESC LIMIT 200) 
            ORDER BY Id ASC", conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var bytes = (byte[])reader.GetValue(0);
                try
                {
                    var metric = ExportMetricsServiceRequest.Parser.ParseFrom(bytes);
                    Metrics.Enqueue(metric);
                }
                catch { }
            }
        }

        // 3. Load Traces
        var traceIds = new List<string>();
        using (var cmd = new SqliteCommand(@"
            SELECT TraceId 
            FROM (
                SELECT TraceId, MAX(StartTime) as MaxStart 
                FROM Spans 
                GROUP BY TraceId 
                ORDER BY MaxStart DESC 
                LIMIT 200
            ) 
            ORDER BY MaxStart ASC", conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                traceIds.Add(reader.GetString(0));
            }
        }

        if (traceIds.Count > 0)
        {
            var inClause = string.Join(",", traceIds.Select((t, i) => $"@t{i}"));
            var query = $@"
                SELECT TraceId, SpanId, ParentSpanId, Name, StartTime, EndTime, ServiceName, HttpStatusCode, HttpMethod, UrlPath, IsError 
                FROM Spans 
                WHERE TraceId IN ({inClause})";

            var spansByTrace = new Dictionary<string, List<ParsedSpan>>();
            using (var cmd = new SqliteCommand(query, conn))
            {
                for (int i = 0; i < traceIds.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"@t{i}", traceIds[i]);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var traceId = reader.GetString(0);
                        var spanId = reader.GetString(1);
                        var parentSpanId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var name = reader.GetString(3);
                        var startTime = DateTimeOffset.Parse(reader.GetString(4));
                        var endTime = DateTimeOffset.Parse(reader.GetString(5));
                        var serviceName = reader.GetString(6);
                        var httpStatusCode = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
                        var httpMethod = reader.IsDBNull(8) ? null : reader.GetString(8);
                        var urlPath = reader.IsDBNull(9) ? null : reader.GetString(9);
                        var isError = reader.GetInt32(10) != 0;

                        var parsedSpan = new ParsedSpan(
                            traceId,
                            spanId,
                            parentSpanId,
                            name,
                            startTime,
                            endTime,
                            endTime - startTime,
                            serviceName,
                            httpStatusCode,
                            httpMethod,
                            urlPath,
                            isError
                        );

                        if (!spansByTrace.TryGetValue(traceId, out var list))
                        {
                            list = new List<ParsedSpan>();
                            spansByTrace[traceId] = list;
                        }
                        list.Add(parsedSpan);
                    }
                }
            }

            foreach (var traceId in traceIds)
            {
                if (spansByTrace.TryGetValue(traceId, out var spans))
                {
                    var minTime = spans.Min(s => s.StartTime);
                    var hasError = spans.Any(s => s.IsError);
                    var trace = new ParsedTrace(traceId, minTime, TimeSpan.Zero, null, spans, hasError);
                    var recomputed = RecomputeTrace(trace);
                    Traces[traceId] = recomputed;
                    TraceIds.Enqueue(traceId);
                }
            }
        }
    }

    public override void AddTrace(ExportTraceServiceRequest request)
    {
        base.AddTrace(request);

        if (!_dbEnabled) return;

        var parsedSpans = new List<ParsedSpan>();
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

                    parsedSpans.Add(new ParsedSpan(
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
                    ));
                }
            }
        }

        if (parsedSpans.Count > 0)
        {
            Task.Run(() =>
            {
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var transaction = conn.BeginTransaction();
                    foreach (var span in parsedSpans)
                    {
                        using var cmd = new SqliteCommand(@"
                            INSERT INTO Spans (TraceId, SpanId, ParentSpanId, Name, StartTime, EndTime, DurationTicks, ServiceName, HttpStatusCode, HttpMethod, UrlPath, IsError)
                            VALUES (@TraceId, @SpanId, @ParentSpanId, @Name, @StartTime, @EndTime, @DurationTicks, @ServiceName, @HttpStatusCode, @HttpMethod, @UrlPath, @IsError)", conn, transaction);
                        cmd.Parameters.AddWithValue("@TraceId", span.TraceId);
                        cmd.Parameters.AddWithValue("@SpanId", span.SpanId);
                        cmd.Parameters.AddWithValue("@ParentSpanId", string.IsNullOrEmpty(span.ParentSpanId) ? DBNull.Value : span.ParentSpanId);
                        cmd.Parameters.AddWithValue("@Name", span.Name);
                        cmd.Parameters.AddWithValue("@StartTime", span.StartTime.ToString("o"));
                        cmd.Parameters.AddWithValue("@EndTime", span.EndTime.ToString("o"));
                        cmd.Parameters.AddWithValue("@DurationTicks", span.Duration.Ticks);
                        cmd.Parameters.AddWithValue("@ServiceName", span.ServiceName);
                        cmd.Parameters.AddWithValue("@HttpStatusCode", span.HttpStatusCode.HasValue ? span.HttpStatusCode.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@HttpMethod", string.IsNullOrEmpty(span.HttpMethod) ? DBNull.Value : span.HttpMethod);
                        cmd.Parameters.AddWithValue("@UrlPath", string.IsNullOrEmpty(span.UrlPath) ? DBNull.Value : span.UrlPath);
                        cmd.Parameters.AddWithValue("@IsError", span.IsError ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    PruneDatabase(conn);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SqliteTelemetryStore] Error saving spans: {ex.Message}");
                }
            });
        }
    }

    public override void AddLog(ExportLogsServiceRequest request)
    {
        base.AddLog(request);

        if (!_dbEnabled) return;

        var parsedLogs = new List<ParsedLog>();
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

                    parsedLogs.Add(new ParsedLog(
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

        if (parsedLogs.Count > 0)
        {
            Task.Run(() =>
            {
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var transaction = conn.BeginTransaction();
                    foreach (var log in parsedLogs)
                    {
                        using var cmd = new SqliteCommand(@"
                            INSERT INTO Logs (Timestamp, Severity, Message, ServiceName, TraceId, SpanId, AttributesJson)
                            VALUES (@Timestamp, @Severity, @Message, @ServiceName, @TraceId, @SpanId, @AttributesJson)", conn, transaction);
                        cmd.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("o"));
                        cmd.Parameters.AddWithValue("@Severity", log.Severity);
                        cmd.Parameters.AddWithValue("@Message", log.Message);
                        cmd.Parameters.AddWithValue("@ServiceName", log.ServiceName);
                        cmd.Parameters.AddWithValue("@TraceId", string.IsNullOrEmpty(log.TraceId) ? DBNull.Value : log.TraceId);
                        cmd.Parameters.AddWithValue("@SpanId", string.IsNullOrEmpty(log.SpanId) ? DBNull.Value : log.SpanId);
                        cmd.Parameters.AddWithValue("@AttributesJson", log.Attributes != null ? JsonSerializer.Serialize(log.Attributes) : DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    PruneDatabase(conn);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SqliteTelemetryStore] Error saving logs: {ex.Message}");
                }
            });
        }
    }

    public override void AddMetric(ExportMetricsServiceRequest metric)
    {
        base.AddMetric(metric);

        if (!_dbEnabled) return;

        Task.Run(() =>
        {
            try
            {
                var bytes = metric.ToByteArray();
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = new SqliteCommand(@"
                    INSERT INTO Metrics (MetricData, Timestamp)
                    VALUES (@MetricData, @Timestamp)", conn);
                cmd.Parameters.AddWithValue("@MetricData", bytes);
                cmd.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                PruneDatabase(conn);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SqliteTelemetryStore] Error saving metric: {ex.Message}");
            }
        });
    }

    private void PruneDatabase(SqliteConnection conn)
    {
        try
        {
            using (var cmd = new SqliteCommand($"DELETE FROM Logs WHERE Id NOT IN (SELECT Id FROM Logs ORDER BY Id DESC LIMIT {_maxDbRecords})", conn))
            {
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new SqliteCommand($"DELETE FROM Spans WHERE TraceId NOT IN (SELECT DISTINCT TraceId FROM Spans ORDER BY StartTime DESC LIMIT {_maxDbRecords})", conn))
            {
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new SqliteCommand($"DELETE FROM Metrics WHERE Id NOT IN (SELECT Id FROM Metrics ORDER BY Id DESC LIMIT {_maxDbRecords})", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SqliteTelemetryStore] Pruning error: {ex.Message}");
        }
    }
}
