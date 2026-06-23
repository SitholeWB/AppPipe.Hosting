using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AppPipeSystem.ApiService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddWindowsService();

        // 1. Identify your service for the dashboard
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService("ApiService");

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
            app.Logger.LogInformation("ApiService backend received a request.");
            return Results.Ok(new { message = "Hello from ApiService" });
        });

        app.Run();
    }
}
