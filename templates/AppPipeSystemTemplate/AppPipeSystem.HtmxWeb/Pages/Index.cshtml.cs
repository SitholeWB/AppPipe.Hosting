using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Json;

namespace AppPipeSystem.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<IndexModel> _logger;

    public List<ProductItem> Products { get; set; } = new();

    public IndexModel(IHttpClientFactory clientFactory, ILogger<IndexModel> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnGetProductsListAsync()
    {
        await LoadProductsAsync();
        return Partial("_ProductsList", this);
    }

    public async Task<IActionResult> OnPostCreateProductAsync(string name, decimal price)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("Name is required");
        }

        try
        {
            var client = _clientFactory.CreateClient("ApiService");

#if UseJwtAuth
            // Obtain mock token first
            var tokenResponse = await client.PostAsync("auth/token", null);
            if (tokenResponse.IsSuccessStatusCode)
            {
                var tokenObj = await tokenResponse.Content.ReadFromJsonAsync<TokenResult>();
                if (tokenObj != null && !string.IsNullOrEmpty(tokenObj.Token))
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenObj.Token);
                }
            }
#endif

            var command = new { Name = name, Price = price };
            var response = await client.PostAsJsonAsync("products", command);
            
            if (response.IsSuccessStatusCode)
            {
                await LoadProductsAsync();
                return Partial("_ProductsList", this);
            }
            
            return StatusCode((int)response.StatusCode, "Failed to create product backend side");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create product");
            return StatusCode(500, "Error communicating with ApiService");
        }
    }

    private async Task LoadProductsAsync()
    {
        try
        {
            var client = _clientFactory.CreateClient("ApiService");
            var result = await client.GetFromJsonAsync<List<ProductItem>>("products");
            if (result != null)
            {
                Products = result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch products");
            Products = new();
        }
    }

    public class ProductItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    public class TokenResult
    {
        public string Token { get; set; } = string.Empty;
    }
}
