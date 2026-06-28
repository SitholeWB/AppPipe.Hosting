using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using AppPipeSystem.Web.Components;

namespace AppPipeSystem.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 1. Identify your service for the dashboard
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService("Web");

        // 2. Add Traces & Metrics, and include HTTP client instrumentation
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
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

        // 4. Register Blazor Server-Side Rendering (Razor Components)
        builder.Services.AddRazorComponents();

        // 5. Configure HTTP Client targeting BackendApi
        builder.Services.AddHttpClient("BackendApi", client =>
        {
            var address = builder.Configuration["services:BackendApi:http:0"];
            if (!string.IsNullOrEmpty(address))
            {
                client.BaseAddress = new Uri(address);
            }
        });

        var app = builder.Build();

        app.UseAntiforgery();

        app.MapRazorComponents<App>();

        app.Run();
    }
}

