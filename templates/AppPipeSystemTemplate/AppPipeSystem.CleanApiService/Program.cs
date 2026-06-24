using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using AppPipeSystem.Application.Products.Commands;
using AppPipeSystem.Application.Products.Queries;
using AppPipeSystem.Application.Abstractions;
using AppPipeSystem.Infrastructure;
#if UseJwtAuth
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
#endif

namespace AppPipeSystem.ApiService;

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

        // 4. Register Infrastructure services (including CQRS Handlers and Database Context)
        builder.Services.AddInfrastructure();

        // 5. Configure Authentication
#if UseJwtAuth
        builder.Services.AddAuthentication(opt =>
        {
            opt.DefaultAuthenticateScheme = "Bearer";
            opt.DefaultChallengeScheme = "Bearer";
        })
        .AddJwtBearer("Bearer", opt =>
        {
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("super_secret_apppipe_security_key_for_development")),
                ValidateIssuer = true,
                ValidIssuer = "AppPipe",
                ValidateAudience = true,
                ValidAudience = "AppPipeSystem",
                ValidateLifetime = true
            };
        });
        builder.Services.AddAuthorization();
#endif

        var app = builder.Build();

#if UseJwtAuth
        app.UseAuthentication();
        app.UseAuthorization();
#endif

        // Initialize Database tables
        app.RunDatabaseSetup();

        // 6. Endpoints mapped directly to Handlers (CQRS without MediatR)
        app.MapGet("/", () =>
        {
            app.Logger.LogInformation("ApiService clean backend root endpoint hit.");
            return Results.Ok(new { message = "Hello from clean CQRS ApiService" });
        });

        var postProducts = app.MapPost("/products", async (
            CreateProductCommand command, 
            ICommandHandler<CreateProductCommand, Guid> handler,
            CancellationToken ct) =>
        {
            app.Logger.LogInformation("Creating product: {Name}", command.Name);
            var id = await handler.HandleAsync(command, ct);
            return Results.Created($"/products/{id}", new { id });
        });

#if UseJwtAuth
        postProducts.RequireAuthorization();
#endif

        app.MapGet("/products", async (
            IQueryHandler<GetProductsQuery, List<ProductDto>> handler,
            CancellationToken ct) =>
        {
            app.Logger.LogInformation("Fetching products list");
            var products = await handler.HandleAsync(new GetProductsQuery(), ct);
            return Results.Ok(products);
        });

#if UseJwtAuth
        app.MapPost("/auth/token", () =>
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes("super_secret_apppipe_security_key_for_development");
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Developer") }),
                Expires = DateTime.UtcNow.AddHours(2),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
                Issuer = "AppPipe",
                Audience = "AppPipeSystem"
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return Results.Ok(new { token = tokenHandler.WriteToken(token) });
        });
#endif

        app.Run();
    }
}
