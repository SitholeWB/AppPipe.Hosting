using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Attributes;
using ModularPipelines.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AppPipe.Hosting;

[DependsOn<PublishProjectsModule>]
public class WindowsServiceDeploymentModule : Module<CommandResult[]>
{
    private readonly AppPipeApp _app;

    public WindowsServiceDeploymentModule(AppPipeApp app)
    {
        _app = app;
    }

    protected override async Task<SkipDecision> ShouldSkip(IPipelineContext context)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return SkipDecision.Skip("Not running on Windows");
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
        var telemetryPort = GetFreePort();

        // 1. Deploy the Host Project (Gateway / Dashboard)
        if (_app.HostProject != null)
        {
            var envVars = new Dictionary<string, string>
            {
                { "TELEMETRY_PORT", telemetryPort.ToString() },
                { "WINDOWS_SERVICE", "true" },
                { "ASPNETCORE_ENVIRONMENT", "Production" },
                { "ASPNETCORE_URLS", $"http://localhost:{_app.HostProject.AssignedPort}" },
                { "PORT", _app.HostProject.AssignedPort.ToString() }
            };

            await DeployService(context, _app.HostProject, envVars, cancellationToken, results);
        }

        // 2. Deploy Child Projects
        foreach (var resource in _app.Resources)
        {
            if (resource is ProjectResource project)
            {
                var envVars = new Dictionary<string, string>
                {
                    { "OTEL_EXPORTER_OTLP_ENDPOINT", $"http://localhost:{telemetryPort}" },
                    { "ASPNETCORE_ENVIRONMENT", "Production" },
                    { "ASPNETCORE_URLS", $"http://localhost:{project.AssignedPort}" },
                    { "PORT", project.AssignedPort.ToString() }
                };

                // Add references env variables
                foreach (var reference in project.References)
                {
                    envVars[$"services__{reference.Name}__http__0"] = $"http://localhost:{reference.AssignedPort}";
                }

                // Add project specific environment variables
                foreach (var env in project.EnvironmentVariables)
                {
                    envVars[env.Key] = env.Value;
                }

                await DeployService(context, project, envVars, cancellationToken, results);
            }
        }

        return results.ToArray();
    }

    private async Task DeployService(
        IPipelineContext context,
        ProjectResource project,
        Dictionary<string, string> envVars,
        CancellationToken cancellationToken,
        List<CommandResult> results)
    {
        var publishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
        var exePath = Path.Combine(publishPath, $"{project.Name}.exe");
        var serviceName = project.Name;

        context.Logger.LogInformation($"Deploying {project.Name} as a Windows Service ({serviceName})...");

        // 1. Stop service if running
        try
        {
            await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
            {
                Arguments = new[] { "stop", serviceName }
            }, cancellationToken);
        }
        catch (Exception) { /* Ignore */ }

        // 2. Delete service if exists
        try
        {
            await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
            {
                Arguments = new[] { "delete", serviceName }
            }, cancellationToken);
        }
        catch (Exception) { /* Ignore */ }

        var displayName = project.ServiceDisplayName ?? project.Name;
        var startType = project.ServiceStartType;

        var createArgs = new List<string>
        {
            "create",
            serviceName,
            "binPath=",
            exePath,
            "DisplayName=",
            displayName,
            "start=",
            startType
        };

        if (!string.IsNullOrEmpty(project.ServiceAccount))
        {
            createArgs.Add("obj=");
            createArgs.Add(project.ServiceAccount);
        }

        if (!string.IsNullOrEmpty(project.ServicePassword))
        {
            createArgs.Add("password=");
            createArgs.Add(project.ServicePassword);
        }

        // 3. Create service
        var createResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
        {
            Arguments = createArgs.ToArray()
        }, cancellationToken);
        results.Add(createResult);

        // 3b. Configure description if set
        if (!string.IsNullOrEmpty(project.ServiceDescription))
        {
            try
            {
                var descResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
                {
                    Arguments = new[] { "description", serviceName, project.ServiceDescription }
                }, cancellationToken);
                results.Add(descResult);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to set service description for {serviceName}: {ex.Message}");
            }
        }

        // 4. Configure Environment Variables in the Registry
        try
        {
#pragma warning disable CA1416
            using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", true))
            {
                if (key != null)
                {
                    var envList = new List<string>();
                    foreach (var kvp in envVars)
                    {
                        envList.Add($"{kvp.Key}={kvp.Value}");
                    }
                    key.SetValue("Environment", envList.ToArray(), RegistryValueKind.MultiString);
                    context.Logger.LogInformation($"Successfully configured environment variables in registry for service {serviceName}");
                }
                else
                {
                    context.Logger.LogWarning($"Could not find registry key for service {serviceName}");
                }
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to set environment variables in registry for {serviceName}. Error: {ex.Message}");
        }

        // 5. Start service
        try
        {
            var startResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
            {
                Arguments = new[] { "start", serviceName }
            }, cancellationToken);
            results.Add(startResult);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to start service {serviceName}: {ex.Message}");
        }
    }
}
