using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppPipe.Hosting;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Attributes;
using ModularPipelines.Options;
using Microsoft.Extensions.Logging;

namespace AppPipe.Hosting;

[DependsOn<PublishProjectsModule>]
public class LinuxSystemdDeploymentModule : Module<CommandResult[]>
{
    private readonly AppPipeApp _app;
    private readonly DeploymentOptions _options;

    public LinuxSystemdDeploymentModule(AppPipeApp app, DeploymentOptions options)
    {
        _app = app;
        _options = options;
    }

    protected override async Task<SkipDecision> ShouldSkip(IPipelineContext context)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return SkipDecision.Skip("Not running on Linux");
        }
        return SkipDecision.DoNotSkip;
    }

    private int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();
        
        // 1. Gather all projects to run under systemd
        var projectsToDeploy = new List<(ProjectResource Project, Dictionary<string, string> EnvVars)>();
        var telemetryPort = GetFreePort();

        if (_app.HostProject != null)
        {
            var envVars = new Dictionary<string, string>
            {
                { "TELEMETRY_PORT", telemetryPort.ToString() },
                { "LINUX_SERVICE", "true" },
                { "ASPNETCORE_ENVIRONMENT", "Production" },
                { "ASPNETCORE_URLS", $"http://localhost:{_app.HostProject.AssignedPort}" }
            };
            projectsToDeploy.Add((_app.HostProject, envVars));
        }

        foreach (var resource in _app.Resources)
        {
            if (resource is ProjectResource project)
            {
                var envVars = new Dictionary<string, string>
                {
                    { "OTEL_EXPORTER_OTLP_ENDPOINT", $"http://localhost:{telemetryPort}" },
                    { "ASPNETCORE_ENVIRONMENT", "Production" },
                    { "ASPNETCORE_URLS", $"http://localhost:{project.AssignedPort}" }
                };

                foreach (var reference in project.References)
                {
                    envVars[$"services__{reference.Name}__http__0"] = $"http://localhost:{reference.AssignedPort}";
                }

                foreach (var env in project.EnvironmentVariables)
                {
                    envVars[env.Key] = env.Value;
                }

                projectsToDeploy.Add((project, envVars));
            }
        }

        // 2. Deploy under systemd
        foreach (var item in projectsToDeploy)
        {
            var project = item.Project;
            var envVars = item.EnvVars;
            var publishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
            
            context.Logger.LogInformation($"Deploying {project.Name} to systemd...");

            var description = project.ServiceDescription ?? $"{project.Name} Service";
            var userLine = !string.IsNullOrEmpty(project.ServiceAccount) ? $"User={project.ServiceAccount}\n" : "";

            var serviceContent = $@"
[Unit]
Description={description}
After=network.target

[Service]
{userLine}WorkingDirectory={publishPath}
ExecStart=/usr/bin/dotnet {publishPath}/{project.Name}.dll
Restart=always
RestartSec=10
SyslogIdentifier={project.Name}
";
            foreach (var env in envVars)
            {
                serviceContent += $"Environment={env.Key}={env.Value}\n";
            }

            serviceContent += @"
[Install]
WantedBy=multi-user.target
";

            var fileName = $"{project.Name}.service";
            var serviceFilePath = $"/etc/systemd/system/{fileName}";
            
            try
            {
                await File.WriteAllTextAsync(serviceFilePath, serviceContent, cancellationToken);

                var reloadResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("systemctl")
                {
                    Arguments = new[] { "daemon-reload" }
                }, cancellationToken);
                results.Add(reloadResult);

                var enableResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("systemctl")
                {
                    Arguments = new[] { "enable", "--now", fileName }
                }, cancellationToken);
                results.Add(enableResult);

                var restartResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("systemctl")
                {
                    Arguments = new[] { "restart", fileName }
                }, cancellationToken);
                results.Add(restartResult);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to configure systemd for {project.Name}. Are you running as root? Error: {ex.Message}");
            }
        }

        // 3. Automate Nginx Reverse Proxy
        if (_options.Target == DeploymentTarget.LinuxNginx)
        {
            context.Logger.LogInformation("Configuring Nginx reverse proxy...");
            var nginxConfig = new System.Text.StringBuilder();
            nginxConfig.AppendLine("server {");
            nginxConfig.AppendLine("    listen 80;");
            nginxConfig.AppendLine("    server_name localhost;");
            nginxConfig.AppendLine();

            // Route for Gateway/Dashboard
            if (_app.HostProject != null)
            {
                var customPath = _app.HostProject.AppPath;
                var locationPath = (string.IsNullOrEmpty(customPath) || customPath == "/") ? "/" : "/" + customPath.Trim('/') + "/";

                nginxConfig.AppendLine($"    location {locationPath} {{");
                nginxConfig.AppendLine($"        proxy_pass http://localhost:{_app.HostProject.AssignedPort};");
                nginxConfig.AppendLine("        proxy_http_version 1.1;");
                nginxConfig.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                nginxConfig.AppendLine("        proxy_set_header Connection keep-alive;");
                nginxConfig.AppendLine("        proxy_set_header Host $host;");
                nginxConfig.AppendLine("        proxy_cache_bypass $http_upgrade;");
                nginxConfig.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
                nginxConfig.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
                nginxConfig.AppendLine("    }");
                nginxConfig.AppendLine();
            }

            // Routes for Child Projects
            foreach (var resource in _app.Resources)
            {
                if (resource is ProjectResource project)
                {
                    var customPath = project.AppPath;
                    var paths = new List<string>();

                    if (!string.IsNullOrEmpty(customPath) && customPath != "/")
                    {
                        var trimmed = customPath.Trim('/');
                        paths.Add($"/{trimmed}/");
                    }
                    else if (customPath != "/")
                    {
                        paths.Add($"/{project.Name}/");
                        paths.Add($"/{project.Name.ToLower()}/");
                    }

                    foreach (var path in paths)
                    {
                        nginxConfig.AppendLine($"    location {path} {{");
                        nginxConfig.AppendLine($"        proxy_pass http://localhost:{project.AssignedPort}/;");
                        nginxConfig.AppendLine("        proxy_http_version 1.1;");
                        nginxConfig.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                        nginxConfig.AppendLine("        proxy_set_header Connection keep-alive;");
                        nginxConfig.AppendLine("        proxy_set_header Host $host;");
                        nginxConfig.AppendLine("        proxy_cache_bypass $http_upgrade;");
                        nginxConfig.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
                        nginxConfig.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
                        nginxConfig.AppendLine("    }");
                        nginxConfig.AppendLine();
                    }
                }
            }

            nginxConfig.AppendLine("}");

            try
            {
                var nginxPath = "/etc/nginx/sites-available/apppipe";
                var symlinkPath = "/etc/nginx/sites-enabled/apppipe";
                
                await File.WriteAllTextAsync(nginxPath, nginxConfig.ToString(), cancellationToken);

                // Create symlink
                await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("ln")
                {
                    Arguments = new[] { "-sf", nginxPath, symlinkPath }
                }, cancellationToken);

                // Test nginx
                await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("nginx")
                {
                    Arguments = new[] { "-t" }
                }, cancellationToken);

                // Reload nginx
                var reloadResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("systemctl")
                {
                    Arguments = new[] { "reload", "nginx" }
                }, cancellationToken);
                results.Add(reloadResult);
                
                context.Logger.LogInformation("Successfully configured and reloaded Nginx reverse proxy.");
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to configure Nginx. Are you running as root and is Nginx installed? Error: {ex.Message}");
            }
        }

        // 4. Automate Caddy Reverse Proxy
        if (_options.Target == DeploymentTarget.LinuxCaddy)
        {
            context.Logger.LogInformation("Configuring Caddy reverse proxy...");
            var caddyConfig = new System.Text.StringBuilder();
            caddyConfig.AppendLine(":80 {");

            // Route for Child Projects
            foreach (var resource in _app.Resources)
            {
                if (resource is ProjectResource project)
                {
                    var customPath = project.AppPath;
                    var paths = new List<string>();

                    if (!string.IsNullOrEmpty(customPath) && customPath != "/")
                    {
                        paths.Add(customPath.Trim('/'));
                    }
                    else if (customPath != "/")
                    {
                        paths.Add(project.Name);
                        paths.Add(project.Name.ToLower());
                    }

                    foreach (var name in paths)
                    {
                        caddyConfig.AppendLine($"    handle_path /{name}/* {{");
                        caddyConfig.AppendLine($"        reverse_proxy localhost:{project.AssignedPort}");
                        caddyConfig.AppendLine("    }");
                    }
                }
            }

            // Route for Gateway/Dashboard (fallback)
            if (_app.HostProject != null)
            {
                var customPath = _app.HostProject.AppPath;
                var handlePath = (string.IsNullOrEmpty(customPath) || customPath == "/") ? "/*" : "/" + customPath.Trim('/') + "/*";

                caddyConfig.AppendLine($"    handle {handlePath} {{");
                caddyConfig.AppendLine($"        reverse_proxy localhost:{_app.HostProject.AssignedPort}");
                caddyConfig.AppendLine("    }");
            }

            caddyConfig.AppendLine("}");

            try
            {
                var caddyfilePath = "/etc/caddy/Caddyfile";
                await File.WriteAllTextAsync(caddyfilePath, caddyConfig.ToString(), cancellationToken);

                // Reload caddy
                var reloadResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("systemctl")
                {
                    Arguments = new[] { "reload", "caddy" }
                }, cancellationToken);
                results.Add(reloadResult);
                
                context.Logger.LogInformation("Successfully configured and reloaded Caddy reverse proxy.");
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to configure Caddy. Are you running as root and is Caddy installed? Error: {ex.Message}");
            }
        }

        return results.ToArray();
    }
}
