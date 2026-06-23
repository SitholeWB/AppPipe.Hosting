using AppPipeSystem.Application.Abstractions;
using AppPipeSystem.Domain;
#if UseRedis
using Microsoft.Extensions.Caching.Distributed;
#endif

namespace AppPipeSystem.Application.Products.Commands;

public record CreateProductCommand(string Name, decimal Price);

public class CreateProductCommandHandler : ICommandHandler<CreateProductCommand, Guid>
{
    private readonly IAppDbContext _dbContext;
#if UseRedis
    private readonly IDistributedCache _cache;
#endif

    public CreateProductCommandHandler(
        IAppDbContext dbContext
#if UseRedis
        , IDistributedCache cache
#endif
    )
    {
        _dbContext = dbContext;
#if UseRedis
        _cache = cache;
#endif
    }

    public async Task<Guid> HandleAsync(CreateProductCommand command, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var product = new Product(id, command.Name, command.Price);
        
        _dbContext.Products.Add(product);
        await _dbContext.SaveChangesAsync(cancellationToken);

#if UseRedis
        await _cache.RemoveAsync("products-list", cancellationToken);
#endif

        return id;
    }
}
