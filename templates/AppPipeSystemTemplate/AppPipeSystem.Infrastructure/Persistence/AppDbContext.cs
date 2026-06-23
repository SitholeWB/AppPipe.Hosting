using Microsoft.EntityFrameworkCore;
using AppPipeSystem.Application.Abstractions;
using AppPipeSystem.Domain;

namespace AppPipeSystem.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    public DbSet<Product> Products { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Seed some initial products for demonstration
        modelBuilder.Entity<Product>().HasData(
            new Product(Guid.Parse("d3b07384-d113-495f-a3cf-53258c69ef1f"), "Enterprise Widget", 99.99m),
            new Product(Guid.Parse("a5c07384-d113-495f-a3cf-53258c69ef2f"), "Cloud Service License", 1499.50m)
        );
    }
}
