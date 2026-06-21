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

using System.Linq;
using System.Xml.Linq;

namespace AppPipe.Hosting;

[DependsOn<PublishProjectsModule>]
public class WindowsIISDeploymentModule : Module<CommandResult[]>
{
    private readonly AppPipeApp _app;
    private readonly DeploymentOptions _options;

    public WindowsIISDeploymentModule(AppPipeApp app, DeploymentOptions options)
    {
        _app = app;
        _options = options;
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
        var appCmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
        var basePath = string.IsNullOrEmpty(_options.IISPath) ? "" : _options.IISPath;
        var telemetryPort = GetFreePort();

        if (_app.HostProject != null)
        {
            var hostAppPath = string.IsNullOrEmpty(basePath) ? $"/{_app.HostProject.Name}" : basePath;
            var envVars = new Dictionary<string, string>
            {
                { "TELEMETRY_PORT", telemetryPort.ToString() }
            };
            await DeployProjectToIIS(context, _app.HostProject, hostAppPath, appCmdPath, cancellationToken, results, envVars, true);
        }
        

        foreach (var resource in _app.Resources)
        {
            if (resource is ProjectResource project)
            {
                var appPath = $"{basePath}/{project.Name}";
                var envVars = new Dictionary<string, string>();
                envVars["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://localhost:{telemetryPort}";
                
                foreach (var env in project.EnvironmentVariables)
                    envVars[env.Key] = env.Value;
                
                foreach (var reference in project.References)
                {
                    envVars[$"services__{reference.Name}__http__0"] = $"http://localhost{basePath}/{reference.Name}/";
                }

                await DeployProjectToIIS(context, project, appPath, appCmdPath, cancellationToken, results, envVars, false);
            }
        }

        return results.ToArray();
    }

    private async Task DeployProjectToIIS(IPipelineContext context, ProjectResource project, string appPath, string appCmdPath, CancellationToken cancellationToken, List<CommandResult> results, Dictionary<string, string> envVars, bool outOfProcess)
    {
        var publishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
        context.Logger.LogInformation($"Deploying {project.Name} to IIS at path {appPath}...");

        var webConfigPath = Path.Combine(publishPath, "web.config");
        if (File.Exists(webConfigPath))
        {
            try
            {
                var doc = XDocument.Load(webConfigPath);
                var aspNetCore = doc.Descendants("aspNetCore").FirstOrDefault();
                if (aspNetCore != null)
                {
                    if (outOfProcess)
                    {
                        aspNetCore.SetAttributeValue("hostingModel", "OutOfProcess");
                    }
                    
                    var envVarsElement = aspNetCore.Elements("environmentVariables").FirstOrDefault();
                    if (envVarsElement == null)
                    {
                        envVarsElement = new XElement("environmentVariables");
                        aspNetCore.Add(envVarsElement);
                    }
                    else
                    {
                        envVarsElement.RemoveAll();
                    }

                    foreach(var kvp in envVars)
                    {
                        envVarsElement.Add(new XElement("environmentVariable", 
                            new XAttribute("name", kvp.Key), 
                            new XAttribute("value", kvp.Value)));
                    }
                    doc.Save(webConfigPath);
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to update web.config: {ex.Message}");
            }
        }

        try
        {
            try
            {
                // Create AppPool
                var appPoolResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                {
                    Arguments = new[] { "add", "apppool", $"/name:{project.Name}Pool" }
                }, cancellationToken);
                results.Add(appPoolResult);
            }
            catch (Exception ex) { context.Logger.LogWarning($"AppPool warning: {ex.Message}"); }

            try
            {
                // Create App under Default Web Site
                var siteResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                {
                    Arguments = new[] { "add", "app", $"/site.name:Default Web Site", $"/path:{appPath}", $"/physicalPath:{publishPath}", $"/applicationPool:{project.Name}Pool" }
                }, cancellationToken);
                results.Add(siteResult);
            }
            catch (Exception ex) { context.Logger.LogWarning($"App creation warning: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to run appcmd.exe. Are you running as Administrator? Error: {ex.Message}");
        }
    }
}
