# Custom Telemetry Database Configuration 💾

By default, **AppPipe** stores OpenTelemetry Protocol (OTLP) telemetry (traces, logs, and metrics) in a circular in-memory buffer ([InMemoryTelemetryStore](file:///d:/Git/Github/AppPipe.Hosting/AppPipe.Hosting/Gateway/Services/InMemoryTelemetryStore.cs)). While this is perfect for local development, production setups or long-term analytics need persistent storage.

AppPipe allows you to plug in your own database (such as **SQLite**, **PostgreSQL**, **SQL Server**, or **ClickHouse**) by implementing a single interface: [ITelemetryStore](file:///d:/Git/Github/AppPipe.Hosting/AppPipe.Hosting/Gateway/Services/ITelemetryStore.cs).

---

## 🏛️ The Telemetry Store Contract

To customize where telemetry is saved and how the dashboard queries it, implement the [ITelemetryStore](file:///d:/Git/Github/AppPipe.Hosting/AppPipe.Hosting/Gateway/Services/ITelemetryStore.cs) interface:

```csharp
using System;
using System.Collections.Concurrent;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace AppPipe.Gateway.Services;

public interface ITelemetryStore
{
    // Properties used by the Blazor Dashboard to render telemetry:
    ConcurrentDictionary<string, ParsedTrace> Traces { get; }
    ConcurrentQueue<ParsedLog> Logs { get; }
    ConcurrentQueue<ExportMetricsServiceRequest> Metrics { get; }
    
    // Ingestion methods called by the OTLP gRPC collector endpoints:
    void AddTrace(ExportTraceServiceRequest request);
    void AddLog(ExportLogsServiceRequest request);
    void AddMetric(ExportMetricsServiceRequest metric);
    
    // Event to notify the Blazor UI to refresh (relevant in Interactive/WebSocket mode)
    event Action? OnTelemetryReceived;
}
```

---

## 🛠️ Step-by-Step Implementation Example

Here is how you can implement and register a custom store using **SQLite** or any database provider.

### 1. Implement the Store Interface

Create a class in your hosting orchestrator project (e.g., `SqliteTelemetryStore.cs`) that handles parsing and persisting the telemetry requests:

```csharp
using System;
using System.Collections.Concurrent;
using AppPipe.Gateway.Services;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;

public class SqliteTelemetryStore : ITelemetryStore
{
    // Return telemetry from database to supply the Blazor UI:
    public ConcurrentDictionary<string, ParsedTrace> Traces => LoadTracesFromDatabase();
    public ConcurrentQueue<ParsedLog> Logs => LoadLogsFromDatabase();
    public ConcurrentQueue<ExportMetricsServiceRequest> Metrics => LoadMetricsFromDatabase();

    public event Action? OnTelemetryReceived;

    public void AddTrace(ExportTraceServiceRequest request)
    {
        // 1. Process/parse the incoming OTLP trace data
        // 2. Persist it to your SQLite database
        
        // 3. Trigger UI update notifications
        OnTelemetryReceived?.Invoke();
    }

    public void AddLog(ExportLogsServiceRequest request)
    {
        // Persist logs to SQLite database
        OnTelemetryReceived?.Invoke();
    }

    public void AddMetric(ExportMetricsServiceRequest metric)
    {
        // Persist metrics to SQLite database
        OnTelemetryReceived?.Invoke();
    }

    private ConcurrentDictionary<string, ParsedTrace> LoadTracesFromDatabase()
    {
        // Query SQLite DB and return parsed traces
        return new ConcurrentDictionary<string, ParsedTrace>();
    }

    private ConcurrentQueue<ParsedLog> LoadLogsFromDatabase()
    {
        // Query SQLite DB and return parsed logs
        return new ConcurrentQueue<ParsedLog>();
    }

    private ConcurrentQueue<ExportMetricsServiceRequest> LoadMetricsFromDatabase()
    {
        // Query SQLite DB and return parsed metrics
        return new ConcurrentQueue<ExportMetricsServiceRequest>();
    }
}
```

### 2. Register the Custom Store in your Orchestrator

Register your custom database store with the `AppPipeAppBuilder` during startup. Use the fluent `ConfigureGateway` method to override the default in-memory registration:

```csharp
using AppPipe.Hosting;
using AppPipe.Gateway.Services;
using Microsoft.Extensions.DependencyInjection;

var builder = AppPipeApp.CreateBuilder(args);

// Register custom Telemetry Store on the Gateway's DI container:
builder.ConfigureGateway(gatewayBuilder =>
{
    gatewayBuilder.Services.AddSingleton<ITelemetryStore, SqliteTelemetryStore>();
});

// Define your microservices topology:
builder.AddProject("BackendWorker");
builder.AddProject("FrontendApi");

var app = builder.Build();

// Run the host (local run or IIS deploy)
if (args.Length > 0 && args[0] == "deploy")
{
    await OnPremDeployer.CompileToOnPremAsync(app, args.Length > 1 ? args[1] : "");
}
else if (Environment.GetEnvironmentVariable("APP_POOL_ID") != null)
{
    var gateway = new GatewayHost();
    await gateway.StartAsync(string.Empty, app, app.ConfigureGatewayAction);
    await Task.Delay(-1);
}
else
{
    var runner = new DevHostRunner(app);
    await runner.RunAsync();
}
```

---

## 🎯 Benefits of a Custom Database
- **Persistence**: Diagnostics survive application restarts, AppPool recycles, and system reboots.
- **Scale**: Offloads telemetry storage from server RAM to disk (crucial for long-term production metrics).
- **Searchability**: Query historical diagnostics using full SQL or third-party database tools.
