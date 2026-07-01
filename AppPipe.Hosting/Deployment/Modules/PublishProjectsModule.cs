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
    private readonly AppPipeHostingApp _app;
    private readonly DeploymentOptions _options;

    public PublishProjectsModule(AppPipeHostingApp app, DeploymentOptions options)
    {
        _app = app;
        _options = options;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) && !IsAdministrator())
        {
            context.Logger.LogWarning("=================================================================================");
            context.Logger.LogWarning(" WARNING: Running without Windows Administrator privileges!");
            context.Logger.LogWarning(" IIS and Service tasks (like iisreset and sc stop) will fail due to permissions.");
            context.Logger.LogWarning(" Please run the command in an Elevated (Administrator) terminal to avoid locks.");
            context.Logger.LogWarning("=================================================================================");
        }

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
                if (r is AppPipeHostingProjectResource p)
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

        var isFiltered = _options.ProjectsFilter != null && _options.ProjectsFilter.Count > 0;

        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipe.Hosting.AppPipeHostingProjectResource project)
            {
                if (isFiltered && !_options.ProjectsFilter!.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
                {
                    context.Logger.LogInformation($"Skipping publish for microservice '{project.Name}' (not in ProjectsFilter).");
                    continue;
                }

                var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
                
                CleanDirectory(context, outputPath);
                
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
            var publishHost = !isFiltered || _options.ProjectsFilter!.Contains(_app.HostProject.Name, StringComparer.OrdinalIgnoreCase);
            if (publishHost)
            {
                var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", _app.HostProject.Name);
                
                CleanDirectory(context, outputPath);
                
                context.Logger.LogInformation($"Publishing Host Project {_app.HostProject.Name} to {outputPath}...");
                
                var result = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("dotnet")
                {
                    Arguments = new[] { "publish", _app.HostProject.ProjectPath, "-c", "Release", "-o", outputPath }
                }, cancellationToken);
                
                results.Add(result);
            }
            else
            {
                context.Logger.LogInformation($"Skipping publish for Host Project '{_app.HostProject.Name}' (not in ProjectsFilter).");
            }
        }

        return results.ToArray();
    }

    private bool IsAdministrator()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return true;

        try
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private void CleanDirectory(IPipelineContext context, string path)
    {
        if (Directory.Exists(path))
        {
            context.Logger.LogInformation($"Cleaning target publish folder: {path}...");
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return; // Successfully deleted
                }
                catch (Exception ex)
                {
                    if (i == 2) // Last try failed
                    {
                        context.Logger.LogWarning($"Warning: Failed to clean directory {path}: {ex.Message}. It may contain locked files.");
                    }
                    else
                    {
                        context.Logger.LogInformation($"Retrying directory clean after delay ({i + 1}/3)...");
                        Thread.Sleep(1000);
                    }
                }
            }
        }
    }
}
