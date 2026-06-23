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

        // 4. Register Razor Pages
        builder.Services.AddRazorPages();

        // 5. Configure HTTP Client targeting ApiService
        builder.Services.AddHttpClient("ApiService", client =>
        {
            var address = builder.Configuration["services:ApiService:http:0"];
            if (!string.IsNullOrEmpty(address))
            {
                client.BaseAddress = new Uri(address);
            }
        });

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseRouting();

        app.MapRazorPages();

        app.Run();
    }
}
