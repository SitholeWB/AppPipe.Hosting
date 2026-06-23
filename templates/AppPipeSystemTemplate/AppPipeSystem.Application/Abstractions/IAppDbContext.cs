using Microsoft.EntityFrameworkCore;
using AppPipeSystem.Domain;

namespace AppPipeSystem.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Product> Products { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
