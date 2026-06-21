using Xunit;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AppPipe.Hosting;

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
}
