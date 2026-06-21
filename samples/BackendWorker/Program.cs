using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// -----------------------------------------------------------------------------------------
// SAMPLE APP: BackendWorker
// 
// This is a standard ASP.NET Core minimal API.
// To integrate with AppPipe.NET, you simply register OpenTelemetry and point it to the Gateway.
// AppPipe.DevHost automatically injects the 'OTEL_EXPORTER_OTLP_ENDPOINT' environment 
// variable into this process, routing telemetry to the Gateway's gRPC server.
// -----------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// 1. Identify your service for the dashboard
var resourceBuilder = ResourceBuilder.CreateDefault().AddService("BackendWorker");

// 2. Add Traces & Metrics, ensuring AddOtlpExporter() is called
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

// 3. Add Logging
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeFormattedMessage = true;
    logging.AddOtlpExporter();
});

var app = builder.Build();

// 4. Implement your endpoints natively
app.MapGet("/", () =>
{
    app.Logger.LogInformation("Backend worker received a request.");
    return Results.Ok(new { message = "Hello from BackendWorker" });
});

app.Run();
