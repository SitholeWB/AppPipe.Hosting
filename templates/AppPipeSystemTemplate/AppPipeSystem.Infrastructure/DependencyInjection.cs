using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using AppPipeSystem.Application.Abstractions;
using AppPipeSystem.Application.Products.Commands;
using AppPipeSystem.Application.Products.Queries;
using AppPipeSystem.Infrastructure.Persistence;

namespace AppPipeSystem.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // 1. Register Command Handlers (CQRS without MediatR)
        services.AddTransient<ICommandHandler<CreateProductCommand, Guid>, CreateProductCommandHandler>();

        // 2. Register Query Handlers
        services.AddTransient<IQueryHandler<GetProductsQuery, List<ProductDto>>, GetProductsQueryHandler>();

        // 3. Register Database Context
#if UseSqlite
        services.AddDbContext<AppDbContext>((sp, opt) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            opt.UseSqlite(config.GetConnectionString("Database") ?? "Data Source=products.db");
        });
#elif UsePostgres
        services.AddDbContext<AppDbContext>((sp, opt) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            opt.UseNpgsql(config.GetConnectionString("Database"));
        });
#elif UseSqlServer
        services.AddDbContext<AppDbContext>((sp, opt) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            opt.UseSqlServer(config.GetConnectionString("Database"));
        });
#else
        services.AddDbContext<AppDbContext>(opt =>
        {
            opt.UseInMemoryDatabase("ProductsDb");
        });
#endif

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // 4. Register Caching
#if UseRedis
        services.AddStackExchangeRedisCache(options =>
        {
            var sp = services.BuildServiceProvider();
            var config = sp.GetRequiredService<IConfiguration>();
            options.Configuration = config.GetConnectionString("Redis") ?? "localhost:6379";
        });
#endif

        return services;
    }

    public static IHost RunDatabaseSetup(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        return host;
    }
}
