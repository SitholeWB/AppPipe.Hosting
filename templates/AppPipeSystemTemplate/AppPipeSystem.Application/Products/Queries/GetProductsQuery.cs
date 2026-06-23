using AppPipeSystem.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
#if UseRedis
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
#endif

namespace AppPipeSystem.Application.Products.Queries;

public record GetProductsQuery();

public record ProductDto(Guid Id, string Name, decimal Price);

public class GetProductsQueryHandler : IQueryHandler<GetProductsQuery, List<ProductDto>>
{
    private readonly IAppDbContext _dbContext;
#if UseRedis
    private readonly IDistributedCache _cache;
#endif

    public GetProductsQueryHandler(
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

    public async Task<List<ProductDto>> HandleAsync(GetProductsQuery query, CancellationToken cancellationToken = default)
    {
#if UseRedis
        var cachedData = await _cache.GetStringAsync("products-list", cancellationToken);
        if (!string.IsNullOrEmpty(cachedData))
        {
            var cachedProducts = JsonSerializer.Deserialize<List<ProductDto>>(cachedData);
            if (cachedProducts != null)
            {
                return cachedProducts;
            }
        }
#endif

        var products = await _dbContext.Products
            .Select(p => new ProductDto(p.Id, p.Name, p.Price))
            .ToListAsync(cancellationToken);

#if UseRedis
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        };
        var serialized = JsonSerializer.Serialize(products);
        await _cache.SetStringAsync("products-list", serialized, options, cancellationToken);
#endif

        return products;
    }
}
