using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;

namespace AppPipeProjectTest.ApiService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

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

        // 4. Configure Database Context
        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseInMemoryDatabase("ProductsDb"));

        // 5. Configure Caching

        // 6. Configure Authentication

        var app = builder.Build();


        // Initialize Database tables
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        // 7. Mapped Endpoints
        app.MapGet("/", () =>
        {
            app.Logger.LogInformation("ApiService backend received a request.");
            return Results.Ok(new { message = "Hello from ApiService" });
        });

        app.MapGet("/products", async (AppDbContext db
        ) =>
        {

            var products = await db.Products.ToListAsync();


            return Results.Ok(products);
        });

        var postProducts = app.MapPost("/products", async (Product product, AppDbContext db
        ) =>
        {
            product.Id = Guid.NewGuid();
            db.Products.Add(product);
            await db.SaveChangesAsync();

            return Results.Created($"/products/{product.Id}", product);
        });


        app.Run();
    }
}
