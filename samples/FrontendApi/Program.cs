using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// -----------------------------------------------------------------------------------------
// SAMPLE APP: FrontendApi
// 
// This is a standard ASP.NET Core minimal API.
// It demonstrates how to consume downstream microservices (BackendWorker)
// using AppPipe.NET's dynamic service discovery and distributed tracing.
// -----------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService();

// 1. Identify your service for the dashboard
var resourceBuilder = ResourceBuilder.CreateDefault().AddService("FrontendApi");

// 2. Add Traces & Metrics, and include HTTP client instrumentation
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation() // Traces outgoing HTTP calls
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

// 3. Add Logging
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeFormattedMessage = true;
    logging.AddOtlpExporter();
});

builder.Services.AddHttpClient("BackendWorker", client =>
{
    // The AppPipe framework injects references as services__{Name}__http__0
    var address = builder.Configuration["services:BackendWorker:http:0"];
    if (!string.IsNullOrEmpty(address))
    {
        client.BaseAddress = new Uri(address);
    }
});

var app = builder.Build();

app.MapGet("/", async (IHttpClientFactory factory, ILogger<Program> logger) =>
{
    logger.LogInformation("Frontend API processing request");
    var client = factory.CreateClient("BackendWorker");
    
    if (client.BaseAddress == null)
    {
        return Results.Problem("BackendWorker address not configured by AppPipe DevHost");
    }

    try
    {
        var response = await client.GetAsync("");
        var content = await response.Content.ReadAsStringAsync();
        return Results.Ok(new { frontend = "Frontend API success", backend_response = content });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to call backend");
        return Results.Problem("Failed to reach backend.");
    }
});

app.Run();
