using Xunit;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AppPipe.Hosting;
using Microsoft.Extensions.Configuration;

namespace AppPipe.Hosting.Tests;

public class DashboardTests
{
    [Fact]
    public async Task Dashboard_EndpointsShouldReturnSuccess()
    {
        // 1. Arrange: Create topology and GatewayAppPipeHost
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var hostProject = new AppPipeHostingProjectResource("AppPipe.DevHost", "");
        hostProject.WithEndpoint(0);
        builder.HostProject = hostProject;
        var app = builder.Build();

        var gatewayHost = new GatewayAppPipeHost();
        var ports = await gatewayHost.StartAsync(string.Empty, app);

        using var httpClient = new HttpClient();
        var baseUri = $"http://localhost:{ports.DashboardPort}";

        // 2. Act & Assert: Query endpoints and verify 200 OK
        
        // Debug environment info API
        var debugEnvResponse = await httpClient.GetAsync($"{baseUri}/debug-env");
        Assert.Equal(HttpStatusCode.OK, debugEnvResponse.StatusCode);

        // Dashboard Home page "/"
        var homeResponse = await httpClient.GetAsync($"{baseUri}/");
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);

        // Dashboard page "/dashboard"
        var dashboardResponse = await httpClient.GetAsync($"{baseUri}/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);

        // Logs page "/logs"
        var logsResponse = await httpClient.GetAsync($"{baseUri}/logs");
        Assert.Equal(HttpStatusCode.OK, logsResponse.StatusCode);

        // Traces page "/traces"
        var tracesResponse = await httpClient.GetAsync($"{baseUri}/traces");
        Assert.Equal(HttpStatusCode.OK, tracesResponse.StatusCode);

        // Metrics page "/metrics"
        var metricsResponse = await httpClient.GetAsync($"{baseUri}/metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);

        // Cleanup
        await gatewayHost.StopAsync();
    }

    [Fact]
    public async Task Dashboard_WithBasicAuth_ShouldRequireAuthentication()
    {
        // 1. Arrange: Create topology with basic auth configured
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var hostProject = new AppPipeHostingProjectResource("AppPipe.DevHost", "");
        hostProject.WithEndpoint(0);
        builder.HostProject = hostProject;
        var app = builder.Build();

        var gatewayHost = new GatewayAppPipeHost();
        var ports = await gatewayHost.StartAsync(string.Empty, app, configureBuilder: (webBuilder) =>
        {
            webBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Dashboard:BasicAuth:Enabled", "true" },
                { "Dashboard:BasicAuth:Username", "testuser" },
                { "Dashboard:BasicAuth:Password", "testpass" }
            });
        });

        using var httpClient = new HttpClient();
        var baseUri = $"http://localhost:{ports.DashboardPort}";

        // 2. Act & Assert: Query unprotected vs protected endpoints
        
        // A. Debug-env should not be protected
        var debugEnvResponse = await httpClient.GetAsync($"{baseUri}/debug-env");
        Assert.Equal(HttpStatusCode.OK, debugEnvResponse.StatusCode);

        // B. Dashboard should return 401 Unauthorized
        var dashboardResponse = await httpClient.GetAsync($"{baseUri}/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, dashboardResponse.StatusCode);
        Assert.True(dashboardResponse.Headers.Contains("WWW-Authenticate"));

        // C. Dashboard with invalid credentials should return 401
        using var clientWithBadCreds = new HttpClient();
        var badAuthBytes = System.Text.Encoding.UTF8.GetBytes("testuser:wrongpass");
        clientWithBadCreds.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(badAuthBytes));
        var badAuthResponse = await clientWithBadCreds.GetAsync($"{baseUri}/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, badAuthResponse.StatusCode);

        // D. Dashboard with valid credentials should return 200 OK
        using var clientWithAuth = new HttpClient();
        var authBytes = System.Text.Encoding.UTF8.GetBytes("testuser:testpass");
        clientWithAuth.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        var goodAuthResponse = await clientWithAuth.GetAsync($"{baseUri}/dashboard");
        Assert.Equal(HttpStatusCode.OK, goodAuthResponse.StatusCode);

        // Cleanup
        await gatewayHost.StopAsync();
    }

    [Fact]
    public async Task Dashboard_BlazorNegotiate_ShouldReturnOk()
    {
        // 1. Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var hostProject = new AppPipeHostingProjectResource("AppPipe.DevHost", "");
        hostProject.WithEndpoint(0);
        builder.HostProject = hostProject;
        var app = builder.Build();

        var gatewayHost = new GatewayAppPipeHost();
        var ports = await gatewayHost.StartAsync(string.Empty, app);

        using var httpClient = new HttpClient();
        var baseUri = $"http://localhost:{ports.DashboardPort}";

        // 2. Act
        var response = await httpClient.PostAsync($"{baseUri}/_blazor/negotiate?negotiateVersion=1", null);

        // 3. Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", content);

        // Cleanup
        await gatewayHost.StopAsync();
    }
}
