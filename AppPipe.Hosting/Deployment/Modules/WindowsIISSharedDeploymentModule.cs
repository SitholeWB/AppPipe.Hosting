using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AppPipe.Hosting;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Attributes;
using Microsoft.Extensions.Logging;

namespace AppPipe.Hosting;

[DependsOn<PublishProjectsModule>]
public class WindowsIISSharedDeploymentModule : Module<string>
{
    private readonly AppPipeHostingApp _app;
    private readonly DeploymentOptions _options;

    public WindowsIISSharedDeploymentModule(AppPipeHostingApp app, DeploymentOptions options)
    {
        _app = app;
        _options = options;
    }

    protected override async Task<string?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var sharedHostingDir = Path.Combine(Environment.CurrentDirectory, "publish", "shared-hosting");
        
        context.Logger.LogInformation($"Consolidating shared hosting build files to {sharedHostingDir}...");
        
        if (Directory.Exists(sharedHostingDir))
        {
            try { Directory.Delete(sharedHostingDir, true); } catch { }
        }
        Directory.CreateDirectory(sharedHostingDir);

        var isFiltered = _options.ProjectsFilter != null && _options.ProjectsFilter.Count > 0;

        // 1. Copy the Gateway AppHost (Dashboard) directly to the root of shared-hosting
        if (_app.HostProject != null)
        {
            var deployHost = !isFiltered || _options.ProjectsFilter!.Contains(_app.HostProject.Name, StringComparer.OrdinalIgnoreCase);
            if (deployHost)
            {
                var hostPublishPath = Path.Combine(Environment.CurrentDirectory, "publish", _app.HostProject.Name);
                if (Directory.Exists(hostPublishPath))
                {
                    context.Logger.LogInformation($"Copying Gateway Host ({_app.HostProject.Name}) to root directory...");
                    CopyDirectory(hostPublishPath, sharedHostingDir);
                }
                else
                {
                    context.Logger.LogError($"Error: Gateway Host publish directory not found at: {hostPublishPath}");
                }
            }
            else
            {
                context.Logger.LogInformation($"Skipping Gateway Host '{_app.HostProject.Name}' shared hosting deployment (not in ProjectsFilter).");
            }
        }

        // Determine base URL for telemetry collection
        // Default to a configuration override if set, otherwise fallback to localhost
        var gatewayHostOverride = context.Configuration[$"AppPipe:Endpoints:{_app.HostProject?.Name ?? "AppHost"}"];
        var telemetryUrl = !string.IsNullOrEmpty(gatewayHostOverride) ? gatewayHostOverride : "http://localhost";
        
        // Ensure no trailing slash for telemetry URL
        telemetryUrl = telemetryUrl.TrimEnd('/');

        // 2. Process and copy each child microservice into its AppPath subdirectory
        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipeHostingProjectResource project)
            {
                if (isFiltered && !_options.ProjectsFilter!.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
                {
                    context.Logger.LogInformation($"Skipping shared hosting deployment copy for '{project.Name}' (not in ProjectsFilter).");
                    continue;
                }
                var appPath = project.AppPath ?? $"/{project.Name}";
                appPath = appPath.TrimStart('/');
                
                if (string.IsNullOrEmpty(appPath))
                {
                    context.Logger.LogWarning($"Skipping resource '{project.Name}' as its AppPath resolves to root. You cannot have two root applications.");
                    continue;
                }

                var projectPublishPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
                var targetSubDir = Path.Combine(sharedHostingDir, appPath);
                
                if (Directory.Exists(targetSubDir))
                {
                    try { Directory.Delete(targetSubDir, true); } catch { }
                }
                Directory.CreateDirectory(targetSubDir);

                if (Directory.Exists(projectPublishPath))
                {
                    context.Logger.LogInformation($"Copying Microservice '{project.Name}' to subdirectory '{appPath}'...");
                    CopyDirectory(projectPublishPath, targetSubDir);
                }
                else
                {
                    context.Logger.LogWarning($"Warning: Publish folder for project '{project.Name}' not found at: {projectPublishPath}");
                    continue;
                }

                // 3. Configure web.config inside the sub-application folder
                var webConfigPath = Path.Combine(targetSubDir, "web.config");
                var envVars = new Dictionary<string, string>
                {
                    { "ASPNETCORE_ENVIRONMENT", "Production" },
                    { "OTEL_EXPORTER_OTLP_ENDPOINT", telemetryUrl }
                };

                // Add configured environment variables
                foreach (var env in project.EnvironmentVariables)
                {
                    envVars[env.Key] = env.Value;
                }

                // Add references pointing to localhost endpoints
                foreach (var reference in project.References)
                {
                    var refAppPath = reference.AppPath ?? $"/{reference.Name}";
                    if (!refAppPath.StartsWith("/")) refAppPath = "/" + refAppPath;
                    envVars[$"services__{reference.Name}__http__0"] = $"http://localhost{refAppPath}";
                }

                ConfigureWebConfig(context, webConfigPath, project, envVars);
            }
        }

        // 4. Create ZIP Archive backup
        var zipPath = Path.Combine(Environment.CurrentDirectory, "publish", "apppipe-deploy-shared.zip");
        if (File.Exists(zipPath))
        {
            try { File.Delete(zipPath); } catch { }
        }

        context.Logger.LogInformation($"Packaging deployment archive to {zipPath}...");
        System.IO.Compression.ZipFile.CreateFromDirectory(sharedHostingDir, zipPath);
        context.Logger.LogInformation("ZIP Archive successfully created!");

        return sharedHostingDir;
    }

    private void ConfigureWebConfig(IPipelineContext context, string webConfigPath, AppPipeHostingProjectResource project, Dictionary<string, string> envVars)
    {
        XDocument doc;
        if (File.Exists(webConfigPath))
        {
            try
            {
                doc = XDocument.Load(webConfigPath);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to load existing web.config for {project.Name}, regenerating: {ex.Message}");
                doc = CreateDefaultWebConfig(project);
            }
        }
        else
        {
            doc = CreateDefaultWebConfig(project);
        }

        var systemWebServer = doc.Root?.Descendants("system.webServer").FirstOrDefault();
        if (systemWebServer == null)
        {
            systemWebServer = new XElement("system.webServer");
            doc.Root?.Add(systemWebServer);
        }

        var aspNetCore = systemWebServer.Element("aspNetCore");
        if (aspNetCore == null)
        {
            aspNetCore = new XElement("aspNetCore",
                new XAttribute("processPath", "dotnet"),
                new XAttribute("arguments", $".\\{project.Name}.dll"),
                new XAttribute("stdoutLogEnabled", "false"),
                new XAttribute("stdoutLogFile", ".\\logs\\stdout"),
                new XAttribute("hostingModel", "inprocess")
            );
            systemWebServer.Add(aspNetCore);
        }

        // Force In-Process hosting model for shared hosting compatibility
        aspNetCore.SetAttributeValue("hostingModel", "inprocess");

        var environmentVariables = aspNetCore.Element("environmentVariables");
        if (environmentVariables == null)
        {
            environmentVariables = new XElement("environmentVariables");
            aspNetCore.Add(environmentVariables);
        }

        // Update environment variables
        foreach (var env in envVars)
        {
            var envElem = environmentVariables.Elements("environmentVariable")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), env.Key, StringComparison.OrdinalIgnoreCase));
            
            if (envElem == null)
            {
                envElem = new XElement("environmentVariable", new XAttribute("name", env.Key));
                environmentVariables.Add(envElem);
            }
            envElem.SetAttributeValue("value", env.Value);
        }

        doc.Save(webConfigPath);
    }

    private XDocument CreateDefaultWebConfig(AppPipeHostingProjectResource project)
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("configuration",
                new XElement("system.webServer",
                    new XElement("handlers",
                        new XElement("add",
                            new XAttribute("name", "aspNetCore"),
                            new XAttribute("path", "*"),
                            new XAttribute("verb", "*"),
                            new XAttribute("modules", "AspNetCoreModuleV2"),
                            new XAttribute("resourceType", "Unspecified")
                        )
                    ),
                    new XElement("aspNetCore",
                        new XAttribute("processPath", "dotnet"),
                        new XAttribute("arguments", $".\\{project.Name}.dll"),
                        new XAttribute("stdoutLogEnabled", "false"),
                        new XAttribute("stdoutLogFile", ".\\logs\\stdout"),
                        new XAttribute("hostingModel", "inprocess")
                    )
                )
            )
        );
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        var dirs = dir.GetDirectories();
        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dirs)
        {
            var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }
}
