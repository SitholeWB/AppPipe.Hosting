using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
#if UseRedis
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
#endif
#if UseJwtAuth
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
#endif

namespace AppPipeSystem.BackendApi;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 1. Identify your service for the dashboard
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService("BackendApi");

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
#if UseSqlite
        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("Database") ?? "Data Source=products.db"));
#elif UsePostgres
        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
#elif UseSqlServer
        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("Database")));
#else
        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseInMemoryDatabase("ProductsDb"));
#endif

        // 5. Configure Caching
#if UseRedis
        builder.Services.AddStackExchangeRedisCache(opt =>
        {
            opt.Configuration = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
        });
#endif

        // 6. Configure Authentication
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
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        // 7. Mapped Endpoints
        app.MapGet("/", () =>
        {
            app.Logger.LogInformation("BackendApi backend received a request.");
            return Results.Ok(new { message = "Hello from BackendApi" });
        });

        app.MapGet("/products", async (AppDbContext db
#if UseRedis
            , IDistributedCache cache
#endif
        ) =>
        {
#if UseRedis
            var cachedData = await cache.GetStringAsync("products-list");
            if (!string.IsNullOrEmpty(cachedData))
            {
                var cachedProducts = JsonSerializer.Deserialize<List<Product>>(cachedData);
                if (cachedProducts != null)
                {
                    return Results.Ok(cachedProducts);
                }
            }
#endif

            var products = await db.Products.ToListAsync();

#if UseRedis
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };
            var serialized = JsonSerializer.Serialize(products);
            await cache.SetStringAsync("products-list", serialized, options);
#endif

            return Results.Ok(products);
        });

        var postProducts = app.MapPost("/products", async (Product product, AppDbContext db
#if UseRedis
            , IDistributedCache cache
#endif
        ) =>
        {
            product.Id = Guid.NewGuid();
            db.Products.Add(product);
            await db.SaveChangesAsync();

#if UseRedis
            await cache.RemoveAsync("products-list");
#endif
            return Results.Created($"/products/{product.Id}", product);
        });

#if UseJwtAuth
        postProducts.RequireAuthorization();

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

