using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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

        builder.WebHost.UseStaticWebAssets();

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
        builder.Services.AddRazorPages();

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
        
        var pathBase = topology?.HostProject?.AppPath;
        if (isIIS && !string.IsNullOrEmpty(pathBase) && pathBase != "/")
        {
            _app.UsePathBase(pathBase);
        }

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
            _app.UseHsts();
        }

        if (configureApp != null)
        {
            configureApp(_app);
        }

        _app.UseStaticFiles();
        _app.UseRouting();

        // Redirect root address to the dashboard Razor Page
        _app.MapGet("/", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/dashboard"));
        _app.MapGet("/logs", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/dashboard/logs"));
        _app.MapGet("/traces", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/dashboard/traces"));
        _app.MapGet("/metrics", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/dashboard/metrics"));
        _app.MapGet("/service-map", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/dashboard/service-map"));

        _app.MapRazorPages();

        // REST JSON API endpoints for the HTML5 dashboard
        _app.MapGet("/api/services", (AppPipeHostingApp? topology) =>
        {
            if (topology == null) return Results.Json(Array.Empty<object>());
            
            object MapResource(AppPipeHostingResource r, string type) => new
            {
                name = r.Name,
                port = r.AssignedPort,
                type = type,
                appPath = r.AppPath,
                siteName = r.IISSiteName,
                poolName = r.AppPoolName,
                projectPath = (r is AppPipeHostingProjectResource pr) ? pr.ProjectPath : null,
                command = (r is ExecutableAppPipeHostingResource er1) ? er1.Command : null,
                workingDirectory = (r is ExecutableAppPipeHostingResource er2) ? er2.WorkingDirectory : null,
                args = (r is ExecutableAppPipeHostingResource er3) ? er3.Args : null,
                references = r.References.Select(refR => refR.Name).ToArray(),
                waitDependencies = r.WaitDependencies.Select(waitR => waitR.Name).ToArray(),
                envVars = r.EnvironmentVariables,
                displayName = r.ServiceDisplayName,
                description = r.ServiceDescription,
                startType = r.ServiceStartType,
                account = r.ServiceAccount,
                hostingModel = r.HostingModel
            };

            var servicesList = new List<object>();
            if (topology.HostProject != null)
            {
                servicesList.Add(MapResource(topology.HostProject, "HostProject"));
            }
            foreach (var resource in topology.Resources)
            {
                var type = resource is AppPipeHostingProjectResource ? "Project" :
                           resource is ExecutableAppPipeHostingResource ? "Executable" : "Resource";
                servicesList.Add(MapResource(resource, type));
            }
            return Results.Json(servicesList);
        });

        _app.MapGet("/api/logs", (ITelemetryStore store) => Results.Json(store.Logs.ToArray()));

        _app.MapGet("/api/traces", (ITelemetryStore store) => Results.Json(store.Traces.Values.ToArray()));

        _app.MapGet("/api/metrics", (ITelemetryStore store) =>
        {
            var points = new List<object>();
            foreach (var request in store.Metrics)
            {
                if (request.ResourceMetrics == null) continue;
                foreach (var rm in request.ResourceMetrics)
                {
                    var serviceName = rm.Resource?.Attributes
                        .FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue ?? "Unknown Service";

                    if (rm.ScopeMetrics == null) continue;
                    foreach (var sm in rm.ScopeMetrics)
                    {
                        if (sm.Metrics == null) continue;
                        foreach (var m in sm.Metrics)
                        {
                            var metricName = m.Name;

                            void AddPoints(Google.Protobuf.Collections.RepeatedField<OpenTelemetry.Proto.Metrics.V1.NumberDataPoint> pts)
                            {
                                if (pts == null) return;
                                foreach (var pt in pts)
                                {
                                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(pt.TimeUnixNano / 1_000_000));
                                    double val = pt.ValueCase switch
                                    {
                                        OpenTelemetry.Proto.Metrics.V1.NumberDataPoint.ValueOneofCase.AsDouble => pt.AsDouble,
                                        OpenTelemetry.Proto.Metrics.V1.NumberDataPoint.ValueOneofCase.AsInt => pt.AsInt,
                                        _ => 0
                                    };
                                    points.Add(new
                                    {
                                        metricName,
                                        serviceName,
                                        timestamp,
                                        value = val
                                    });
                                }
                            }

                            if (m.Gauge != null) AddPoints(m.Gauge.DataPoints);
                            if (m.Sum != null) AddPoints(m.Sum.DataPoints);
                        }
                    }
                }
            }
            return Results.Json(points);
        });

        _app.MapPost("/api/logs/clear", (ITelemetryStore store) => { store.ClearLogs(); return Results.Ok(); });
        _app.MapPost("/api/traces/clear", (ITelemetryStore store) => { store.ClearTraces(); return Results.Ok(); });
        _app.MapPost("/api/metrics/clear", (ITelemetryStore store) => { store.ClearMetrics(); return Results.Ok(); });

        _app.MapGet("/debug-env", (IWebHostEnvironment env) => new
        {
            env.ApplicationName,
            env.ContentRootPath,
            env.WebRootPath,
            env.EnvironmentName
        });

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