using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

        if (telemetryPort == 0 && !isIIS)
        {
            telemetryPort = GetFreePort();
        }

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
        builder.Services.AddSingleton<GatewayDiagnosticsService>();

        if (configureBuilder != null)
        {
            configureBuilder(builder);
        }

        // If consumer didn't register ITelemetryStore, register default
        if (!builder.Services.Any(x => x.ServiceType == typeof(ITelemetryStore)))
        {
            var persistenceEnabled = builder.Configuration.GetValue<bool>("Telemetry:PersistenceEnabled", true);
            if (persistenceEnabled)
            {
                builder.Services.AddSingleton<ITelemetryStore, SqliteTelemetryStore>();
            }
            else
            {
                builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();
            }
        }

        if (topology != null)
        {
            builder.Services.AddSingleton(topology);
        }

        // Configure Gateway Self-Telemetry
        var serviceName = topology?.HostProject?.Name ?? "AppPipe.Gateway";
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService(serviceName);
        var otlpEndpoint = $"http://localhost:{telemetryPort}";

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("Yarp.ReverseProxy")
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("Yarp.ReverseProxy")
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resourceBuilder);
            logging.IncludeFormattedMessage = true;
            logging.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        });

        // YARP configuration
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

        _app = builder.Build();
        var _logger = _app.Services.GetRequiredService<ILogger<GatewayAppPipeHost>>();
        // Gateway Diagnostics Middleware
        _app.Use(async (context, next) =>
        {
            var diagnostics = context.RequestServices.GetRequiredService<GatewayDiagnosticsService>();
            diagnostics.IncrementActiveRequests();
            try
            {
                await next();
            }
            finally
            {
                diagnostics.DecrementActiveRequests();
            }
        });

        // Basic Authentication Middleware for Dashboard/UI routes
        _app.Use(async (context, next) =>
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var authEnabled = config.GetValue<bool>("Dashboard:BasicAuth:Enabled", false);
            if (authEnabled)
            {
                var path = context.Request.Path;
                if (path == "/" ||
                    path.StartsWithSegments("/dashboard") ||
                    path.StartsWithSegments("/logs") ||
                    path.StartsWithSegments("/traces") ||
                    path.StartsWithSegments("/metrics") ||
                    path.StartsWithSegments("/diagnostics") ||
                    path.StartsWithSegments("/_blazor"))
                {
                    var expectedUsername = config.GetValue<string>("Dashboard:BasicAuth:Username");
                    var expectedPassword = config.GetValue<string>("Dashboard:BasicAuth:Password");

                    if (!string.IsNullOrEmpty(expectedUsername))
                    {
                        var authHeader = context.Request.Headers["Authorization"].ToString();
                        bool authenticated = false;
                        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var creds = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Substring(6))).Split(':');
                                if (creds.Length == 2 && creds[0] == expectedUsername && creds[1] == expectedPassword)
                                {
                                    authenticated = true;
                                }
                            }
                            catch { }
                        }

                        if (!authenticated)
                        {
                            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"AppPipe Dashboard\"";
                            context.Response.StatusCode = 401;
                            return;
                        }
                    }
                }
            }
            await next();
        });

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

        try
        {
            _app.MapStaticAssets();
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Source, "Microsoft.AspNetCore.StaticAssets", StringComparison.OrdinalIgnoreCase))
        {
            _app.UseStaticFiles();
            _logger.LogWarning(ex, "[AppPipe.Hosting] Static web assets not found. Ensure that the project is built and the static web assets are published.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AppPipe.Hosting] Error while mapping static assets.");
            throw;
        }

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

    private int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Server.LingerState = new System.Net.Sockets.LingerOption(true, 0);
        listener.Stop();
        return port;
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