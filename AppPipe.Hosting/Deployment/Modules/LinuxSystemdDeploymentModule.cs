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

    public LinuxSystemdDeploymentModule(AppPipeApp app)
    {
        _app = app;
    }

    protected override async Task<SkipDecision> ShouldSkip(IPipelineContext context)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return SkipDecision.Skip("Not running on Linux");
        }
        return SkipDecision.DoNotSkip;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();
        
        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipe.Hosting.ProjectResource project)
            {
                var publishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);

                context.Logger.LogInformation($"Deploying {project.Name} to systemd...");

                var serviceContent = $@"
[Unit]
Description={project.Name} Service
After=network.target

[Service]
WorkingDirectory={publishPath}
ExecStart=/usr/bin/dotnet {publishPath}/{project.Name}.dll
Restart=always
RestartSec=10
SyslogIdentifier={project.Name}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://*:{project.AssignedPort}
";
            
            foreach(var r in project.References)
            {
                serviceContent += $"Environment=services__{r.Name}__http__0=http://localhost:{r.AssignedPort}\n";
            }

            serviceContent += @"
[Install]
WantedBy=multi-user.target
";
            var fileName = $"{project.Name}.service";
            var serviceFilePath = $"/etc/systemd/system/{fileName}";
            
            try
            {
                // Note: Writing directly to /etc/systemd requires root permissions.
                // If running locally as non-root, this may fail, which is handled in catch block.
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
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to configure systemd. Are you running as root? Error: {ex.Message}");
            }
            } // end if ProjectResource
        } // end foreach

        return results.ToArray();
    }
}
