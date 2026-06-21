using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AppPipe.Hosting;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Options;
using Microsoft.Extensions.Logging;

namespace AppPipe.Hosting;

public class PublishProjectsModule : Module<CommandResult[]>
{
    private readonly AppPipeApp _app;

    public PublishProjectsModule(AppPipeApp app)
    {
        _app = app;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();

        // If on Windows, stop services first to prevent file locks during publish
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            try
            {
                await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("cmd.exe")
                {
                    Arguments = new[] { "/c", "iisreset /stop" }
                }, cancellationToken);
            }
            catch (Exception) { /* Ignore */ }

            var servicesToStop = new List<string>();
            if (_app.HostProject != null)
                servicesToStop.Add(_app.HostProject.Name);
            foreach (var r in _app.Resources)
            {
                if (r is ProjectResource p)
                    servicesToStop.Add(p.Name);
            }

            foreach (var serviceName in servicesToStop)
            {
                try
                {
                    await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("sc.exe")
                    {
                        Arguments = new[] { "stop", serviceName }
                    }, cancellationToken);

                    // Wait for process to exit, or kill it if it takes too long
                    for (int i = 0; i < 10; i++)
                    {
                        var processes = System.Diagnostics.Process.GetProcessesByName(serviceName);
                        if (processes.Length == 0)
                            break;

                        if (i == 9)
                        {
                            foreach (var p in processes)
                            {
                                try { p.Kill(); } catch { }
                            }
                        }
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (Exception) { /* Ignore */ }
            }
        }

        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipe.Hosting.ProjectResource project)
            {
                var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
                
                context.Logger.LogInformation($"Publishing {project.Name} to {outputPath}...");
                
                var result = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("dotnet")
                {
                    Arguments = new[] { "publish", project.ProjectPath, "-c", "Release", "-o", outputPath }
                }, cancellationToken);
                
                results.Add(result);
            }
        }

        if (_app.HostProject != null)
        {
            var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", _app.HostProject.Name);
            
            context.Logger.LogInformation($"Publishing Host Project {_app.HostProject.Name} to {outputPath}...");
            
            var result = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("dotnet")
            {
                Arguments = new[] { "publish", _app.HostProject.ProjectPath, "-c", "Release", "-o", outputPath }
            }, cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
