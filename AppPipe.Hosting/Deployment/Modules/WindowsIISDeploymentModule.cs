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
    private readonly AppPipeHostingApp _app;
    private readonly DeploymentOptions _options;

    public WindowsIISDeploymentModule(AppPipeHostingApp app, DeploymentOptions options)
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
        listener.Server.LingerState = new System.Net.Sockets.LingerOption(true, 0);
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
            var hostAppPath = !string.IsNullOrEmpty(basePath) ? basePath : (_app.HostProject.AppPath ?? $"/{_app.HostProject.Name}");
            if (hostAppPath == "" || hostAppPath == "/")
                hostAppPath = "/";
            else
            {
                hostAppPath = hostAppPath.StartsWith("/") ? hostAppPath : "/" + hostAppPath;
                hostAppPath = hostAppPath.Replace("//", "/");
            }
            var envVars = new Dictionary<string, string>
            {
                { "TELEMETRY_PORT", telemetryPort.ToString() }
            };
            await DeployProjectToIIS(context, _app.HostProject, hostAppPath, appCmdPath, cancellationToken, results, envVars, true);
        }
        

        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipeHostingProjectResource project)
            {
                var appPath = project.AppPath ?? $"/{project.Name}";
                if (!string.IsNullOrEmpty(basePath) && basePath != "/")
                {
                    appPath = basePath.TrimEnd('/') + "/" + appPath.TrimStart('/');
                }

                if (appPath == "" || appPath == "/")
                    appPath = "/";
                else
                {
                    appPath = appPath.StartsWith("/") ? appPath : "/" + appPath;
                    appPath = appPath.Replace("//", "/");
                }

                var envVars = new Dictionary<string, string>();
                envVars["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://localhost:{telemetryPort}";
                
                foreach (var env in project.EnvironmentVariables)
                    envVars[env.Key] = env.Value;
                
                foreach (var reference in project.References)
                {
                    var refAppPath = reference.AppPath ?? $"/{reference.Name}";
                    if (!string.IsNullOrEmpty(basePath) && basePath != "/")
                    {
                        refAppPath = basePath.TrimEnd('/') + "/" + refAppPath.TrimStart('/');
                    }

                    if (refAppPath == "" || refAppPath == "/")
                        refAppPath = "/";
                    else
                    {
                        refAppPath = "/" + refAppPath.TrimStart('/');
                        if (!refAppPath.EndsWith("/"))
                            refAppPath += "/";
                        refAppPath = refAppPath.Replace("//", "/");
                    }
                    envVars[$"services__{reference.Name}__http__0"] = $"http://localhost{refAppPath}";
                }

                await DeployProjectToIIS(context, project, appPath, appCmdPath, cancellationToken, results, envVars, false);
            }
        }

        try
        {
            // Restart IIS now that deployment is complete
            var startResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("cmd.exe")
            {
                Arguments = new[] { "/c", "iisreset /start" }
            }, cancellationToken);
            results.Add(startResult);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to restart IIS: {ex.Message}");
        }

        return results.ToArray();
    }

    private async Task DeployProjectToIIS(IPipelineContext context, AppPipeHostingProjectResource project, string appPath, string appCmdPath, CancellationToken cancellationToken, List<CommandResult> results, Dictionary<string, string> envVars, bool outOfProcess)
    {
        var publishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
        var appPoolName = project.AppPoolName ?? $"{project.Name}Pool";
        var siteName = project.IISSiteName;
        
        context.Logger.LogInformation($"Deploying {project.Name} to IIS at site '{siteName}', path '{appPath}' using AppPool '{appPoolName}'...");

        var webConfigPath = Path.Combine(publishPath, "web.config");
        if (File.Exists(webConfigPath))
        {
            try
            {
                var doc = XDocument.Load(webConfigPath);
                var aspNetCore = doc.Descendants("aspNetCore").FirstOrDefault();
                if (aspNetCore != null)
                {
                    var model = project.HostingModel ?? (outOfProcess ? "OutOfProcess" : null);
                    if (!string.IsNullOrEmpty(model))
                    {
                        aspNetCore.SetAttributeValue("hostingModel", model);
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
                    Arguments = new[] { "add", "apppool", $"/name:{appPoolName}" }
                }, cancellationToken);
                results.Add(appPoolResult);
            }
            catch (Exception ex) { context.Logger.LogWarning($"AppPool warning: {ex.Message}"); }

            if (!string.IsNullOrEmpty(project.ServiceAccount))
            {
                try
                {
                    var isBuiltIn = project.ServiceAccount.Equals("ApplicationPoolIdentity", StringComparison.OrdinalIgnoreCase) ||
                                     project.ServiceAccount.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                                     project.ServiceAccount.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ||
                                     project.ServiceAccount.Equals("NetworkService", StringComparison.OrdinalIgnoreCase);

                    if (isBuiltIn)
                    {
                        // Set AppPool Identity Type
                        var identityResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                        {
                            Arguments = new[] { "set", "apppool", appPoolName, $"/processModel.identityType:{project.ServiceAccount}" }
                        }, cancellationToken);
                        results.Add(identityResult);
                    }
                    else
                    {
                        // Set Specific User Username and Password
                        var identityResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                        {
                            Arguments = new[] { "set", "apppool", appPoolName, "/processModel.identityType:SpecificUser", $"/processModel.userName:{project.ServiceAccount}", $"/processModel.password:{project.ServicePassword ?? ""}" }
                        }, cancellationToken);
                        results.Add(identityResult);
                    }
                }
                catch (Exception ex) { context.Logger.LogWarning($"Failed to set AppPool identity: {ex.Message}"); }
            }

            if (appPath == "/")
            {
                try
                {
                    // For root app, set the app pool and physical path on the default site root application
                    var poolResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                    {
                        Arguments = new[] { "set", "app", $"{siteName}/", $"/applicationPool:{appPoolName}" }
                    }, cancellationToken);
                    results.Add(poolResult);

                    var pathResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                    {
                        Arguments = new[] { "set", "vdir", $"{siteName}/", $"/physicalPath:{publishPath}" }
                    }, cancellationToken);
                    results.Add(pathResult);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError($"Failed to configure IIS root app: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    // Delete existing App if it exists, to update the physical path/pool cleanly
                    await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                    {
                        Arguments = new[] { "delete", "app", $"{siteName}{appPath}" }
                    }, cancellationToken);
                }
                catch (Exception) { /* Ignore if it doesn't exist */ }

                try
                {
                    // Create App under configured Site
                    var siteResult = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions(appCmdPath)
                    {
                        Arguments = new[] { "add", "app", $"/site.name:{siteName}", $"/path:{appPath}", $"/physicalPath:{publishPath}", $"/applicationPool:{appPoolName}" }
                    }, cancellationToken);
                    results.Add(siteResult);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError($"Failed to create IIS app: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to run appcmd.exe. Are you running as Administrator? Error: {ex.Message}");
        }
    }
}
