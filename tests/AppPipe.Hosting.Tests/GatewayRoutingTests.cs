using Xunit;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AppPipe.Hosting;

namespace AppPipe.Hosting.Tests;

public class GatewayRoutingTests
{
    private int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Gateway_ShouldProxyRequestToMockService()
    {
        // 1. Setup Mock Service using HttpListener
        var mockServicePort = GetFreePort();
        using var mockServiceListener = new HttpListener();
        mockServiceListener.Prefixes.Add($"http://localhost:{mockServicePort}/");
        mockServiceListener.Start();

        // Run listener in a background task
        var _ = Task.Run(async () =>
        {
            try
            {
                var context = await mockServiceListener.GetContextAsync();
                var response = context.Response;
                byte[] buffer = Encoding.UTF8.GetBytes("Hello from Mock Service");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch (ObjectDisposedException) { /* Listener stopped */ }
        });

        // 2. Generate Temp YARP config file
        var tempYarpFile = Path.Combine(Path.GetTempPath(), $"yarp_test_{Guid.NewGuid()}.json");
        var yarpConfig = $@"{{
            ""ReverseProxy"": {{
                ""Routes"": {{
                    ""mockRoute"": {{
                        ""ClusterId"": ""mockCluster"",
                        ""Match"": {{
                            ""Path"": ""/mockservice/{{**catch-all}}""
                        }}
                    }}
                }},
                ""Clusters"": {{
                    ""mockCluster"": {{
                        ""Destinations"": {{
                            ""dest1"": {{
                                ""Address"": ""http://localhost:{mockServicePort}/""
                            }}
                        }}
                    }}
                }}
            }}
        }}";
        await File.WriteAllTextAsync(tempYarpFile, yarpConfig);

        // 3. Start Gateway Host
        var builder = AppPipeApp.CreateBuilder(null!);
        var hostProject = new ProjectResource("AppPipe.DevHost", "");
        hostProject.WithEndpoint(0);
        builder.HostProject = hostProject;
        var app = builder.Build();

        var gatewayHost = new GatewayHost();
        var ports = await gatewayHost.StartAsync(tempYarpFile, app);

        // 4. Act: Request through the Gateway
        using var httpClient = new HttpClient();
        var responseString = await httpClient.GetStringAsync($"http://localhost:{ports.DashboardPort}/mockservice/hello");

        // 5. Assert
        Assert.Equal("Hello from Mock Service", responseString);

        // Cleanup
        await gatewayHost.StopAsync();
        mockServiceListener.Stop();
        if (File.Exists(tempYarpFile))
        {
            File.Delete(tempYarpFile);
        }
    }
}
