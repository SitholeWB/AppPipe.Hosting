using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPipe.Hosting;

public class GatewayAppPipeHost
{
    private WebApplication? _app;

    public async Task<(int DashboardPort, int TelemetryPort)> StartAsync(string yarpConfigFile, AppPipeHostingApp? topology = null, Action<WebApplicationBuilder>? configureBuilder = null, Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "AppPipe.Hosting"
        });

        var dashboardPort = topology?.HostProject?.AssignedPort ?? 0;

        var isIIS = Environment.GetEnvironmentVariable("APP_POOL_ID") != null;
        var envTelemetryPort = Environment.GetEnvironmentVariable("TELEMETRY_PORT");
        var telemetryPort = int.TryParse(envTelemetryPort, out var tp) ? tp : 0;

        builder.WebHost.ConfigureKestrel(options =>
        {
            var aspNetCorePortStr = Environment.GetEnvironmentVariable("ASPNETCORE_PORT");
            if (int.TryParse(aspNetCorePortStr, out var aspNetCorePort))
            {
                options.Listen(System.Net.IPAddress.Loopback, aspNetCorePort);
            }

            if (!isIIS)
            {
                // Dashboard / YARP (HTTP/1.1 and HTTP/2)
                options.Listen(System.Net.IPAddress.Loopback, dashboardPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2);
            }

            if (!isIIS || telemetryPort != 0)
            {
                // Telemetry (Strict HTTP/2 for unencrypted gRPC)
                options.Listen(System.Net.IPAddress.Loopback, telemetryPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
            }
        });

        builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");

        if (!string.IsNullOrEmpty(yarpConfigFile) && File.Exists(yarpConfigFile))
        {
            builder.Configuration.AddJsonFile(yarpConfigFile, optional: false, reloadOnChange: true);
        }

        // Add services to the container.
        builder.Services.AddWindowsService();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, TelemetryPortTokenRemoverFilter>());

        builder.Services.AddGrpc();

        if (configureBuilder != null)
        {
            configureBuilder(builder);
        }

        // If consumer didn't register ITelemetryStore, register default
        if (!builder.Services.Any(x => x.ServiceType == typeof(ITelemetryStore)))
        {
            builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();
        }

        if (topology != null)
        {
            builder.Services.AddSingleton(topology);
        }

        // YARP configuration
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

        _app = builder.Build();

        if (!_app.Environment.IsDevelopment())
        {
            _app.UseExceptionHandler("/Error", createScopeForErrors: true);
            _app.UseHsts();
        }

        _app.UseAntiforgery();

        if (configureApp != null)
        {
            configureApp(_app);
        }

        _app.UseStaticFiles();
        _app.MapStaticAssets();

        _app.MapGet("/debug-env", (IWebHostEnvironment env) => new
        {
            env.ApplicationName,
            env.ContentRootPath,
            env.WebRootPath,
            env.EnvironmentName
        });

        // Note: The namespace of Razor components is based on project root namespace and folder path.
        _app.MapRazorComponents<AppPipe.Hosting.Components.App>()
            .AddInteractiveServerRenderMode();

        // Map OTLP gRPC services
        _app.MapGrpcService<TraceReceiverService>();
        _app.MapGrpcService<LogsReceiverService>();
        _app.MapGrpcService<MetricsReceiverService>();

        // Map YARP
        _app.MapReverseProxy();

        await _app.StartAsync();

        if (isIIS)
        {
            return (80, 80); // IIS manages ports
        }

        var serverAddresses = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>();
        var addresses = serverAddresses?.Addresses.ToList();

        if (addresses == null || addresses.Count < 2)
            throw new Exception("Failed to bind Gateway to dynamic ports.");

        var actualDashboardPort = new Uri(addresses[0]).Port;
        var actualTelemetryPort = new Uri(addresses[1]).Port;

        if (topology?.HostProject != null)
        {
            topology.HostProject.AssignedPort = actualDashboardPort;
        }

        return (actualDashboardPort, actualTelemetryPort);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
        }
    }
}

public class TelemetryPortTokenRemoverFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var tpEnv = Environment.GetEnvironmentVariable("TELEMETRY_PORT");
                if (int.TryParse(tpEnv, out var tp) && context.Connection.LocalPort == tp)
                {
                    // IISIntegrationMiddleware will reject ALL loopback requests if
                    // MS-ASPNETCORE-TOKEN is missing or wrong. Since OTLP comes directly to
                    // localhost from other processes, it's a loopback request but not from ANCM. We
                    // must provide the correct token so IISIntegrationMiddleware allows it.
                    var expectedToken = Environment.GetEnvironmentVariable("ASPNETCORE_TOKEN");
                    if (!string.IsNullOrEmpty(expectedToken))
                    {
                        context.Request.Headers["MS-ASPNETCORE-TOKEN"] = expectedToken;
                    }
                }
                await nextMiddleware();
            });
            next(app);
        };
    }
}