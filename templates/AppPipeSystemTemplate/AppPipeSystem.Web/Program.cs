using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AppPipeSystem.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddWindowsService();

        // 1. Identify your service for the dashboard
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService("Web");

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

        // 4. Configure HTTP Client targeting ApiService
        builder.Services.AddHttpClient("ApiService", client =>
        {
            // The AppPipe framework injects references as services__{Name}__http__0
            var address = builder.Configuration["services:ApiService:http:0"];
            if (!string.IsNullOrEmpty(address))
            {
                client.BaseAddress = new Uri(address);
            }
        });

        var app = builder.Build();

        app.MapGet("/", async (IHttpClientFactory factory, ILogger<Program> logger) =>
        {
            logger.LogInformation("Web frontend processing request");
            var client = factory.CreateClient("ApiService");
            
            if (client.BaseAddress == null)
            {
                return Results.Problem("ApiService address not configured by AppPipe");
            }

            try
            {
                var response = await client.GetAsync("");
                var content = await response.Content.ReadAsStringAsync();
                return Results.Ok(new { frontend = "Web Frontend success", backend_response = content });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to call ApiService backend");
                return Results.Problem("Failed to reach ApiService backend.");
            }
        });

        app.Run();
    }
}
