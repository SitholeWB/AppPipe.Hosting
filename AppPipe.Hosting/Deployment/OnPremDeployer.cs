#if !NETSTANDARD2_0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppPipe.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModularPipelines.Host;
using ModularPipelines.Extensions;

namespace AppPipe.Hosting;

public enum DeploymentTarget
{
    IIS,
    WindowsService,
    LinuxService,
    LinuxNginx,
    LinuxCaddy,
    IISSharedHosting
}

public class DeploymentOptions
{
    public DeploymentTarget Target { get; set; } = DeploymentTarget.IIS;
    public string Path { get; set; } = string.Empty;
    public string IISPath => Path;

    // FTP Upload Settings
    public string FtpHost { get; set; } = string.Empty;
    public string FtpUsername { get; set; } = string.Empty;
    public string FtpPassword { get; set; } = string.Empty;
    public string FtpRemotePath { get; set; } = "/";
    public int FtpPort { get; set; } = 21;
    public bool FtpUseSsl { get; set; } = true;

    // Backup & Rollback Settings
    public bool BackupBeforeDeploy { get; set; } = false;
    public bool RollbackOnError { get; set; } = true;

    // Project Filtering
    public List<string> ProjectsFilter { get; set; } = new List<string>();
}

public class OnPremDeployer
{
    public static async Task CompileToOnPremAsync(
        AppPipeHostingApp app, 
        DeploymentTarget target = DeploymentTarget.IIS, 
        string path = "", 
        Action<PipelineHostBuilder>? configurePipeline = null)
    {
        Console.WriteLine($"Starting AppPipe ModularPipelines Deployment targeting {target}...");
        
        var builder = PipelineHostBuilder.Create()
            .ConfigureServices((context, services) =>
            {
                var config = app.Configuration;
                var ftpHost = config["AppPipe:Deployment:Ftp:Host"] ?? Environment.GetEnvironmentVariable("APH_FTP_HOST") ?? string.Empty;
                var ftpUser = config["AppPipe:Deployment:Ftp:Username"] ?? Environment.GetEnvironmentVariable("APH_FTP_USER") ?? string.Empty;
                var ftpPass = config["AppPipe:Deployment:Ftp:Password"] ?? Environment.GetEnvironmentVariable("APH_FTP_PASS") ?? string.Empty;
                var ftpPath = config["AppPipe:Deployment:Ftp:RemotePath"] ?? Environment.GetEnvironmentVariable("APH_FTP_PATH") ?? "/";
                
                var ftpPortStr = config["AppPipe:Deployment:Ftp:Port"] ?? Environment.GetEnvironmentVariable("APH_FTP_PORT");
                int.TryParse(ftpPortStr, out var ftpPort);
                if (ftpPort == 0) ftpPort = 21;

                var ftpSslStr = config["AppPipe:Deployment:Ftp:UseSsl"] ?? Environment.GetEnvironmentVariable("APH_FTP_SSL");
                bool.TryParse(ftpSslStr, out var ftpUseSsl);
                if (string.IsNullOrEmpty(ftpSslStr) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APH_FTP_SSL"))) ftpUseSsl = true;

                var filterStr = config["AppPipe:Deployment:FilterProjects"] ?? Environment.GetEnvironmentVariable("APH_FILTER_PROJECTS") ?? string.Empty;
                var filterList = string.IsNullOrEmpty(filterStr) 
                    ? new List<string>() 
                    : filterStr.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

                var backupStr = config["AppPipe:Deployment:BackupBeforeDeploy"] ?? Environment.GetEnvironmentVariable("APH_BACKUP_BEFORE_DEPLOY");
                bool.TryParse(backupStr, out var backupBeforeDeploy);

                var rollbackStr = config["AppPipe:Deployment:RollbackOnError"] ?? Environment.GetEnvironmentVariable("APH_ROLLBACK_ON_ERROR");
                bool.TryParse(rollbackStr, out var rollbackOnError);
                if (string.IsNullOrEmpty(rollbackStr) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APH_ROLLBACK_ON_ERROR"))) rollbackOnError = true;

                services.AddSingleton(app);
                services.AddSingleton(new DeploymentOptions 
                { 
                    Target = target,
                    Path = target == DeploymentTarget.IIS
                        ? (string.IsNullOrEmpty(path) ? "" : (path.StartsWith("/") ? path : "/" + path))
                        : path,
                    FtpHost = ftpHost,
                    FtpUsername = ftpUser,
                    FtpPassword = ftpPass,
                    FtpRemotePath = ftpPath,
                    FtpPort = ftpPort,
                    FtpUseSsl = ftpUseSsl,
                    BackupBeforeDeploy = backupBeforeDeploy,
                    RollbackOnError = rollbackOnError,
                    ProjectsFilter = filterList
                });
                services.AddModule<PublishProjectsModule>();
                
                // Add deployment modules based on the selected target
                if (target == DeploymentTarget.WindowsService)
                {
                    services.AddModule<WindowsServiceDeploymentModule>();
                }
                else if (target == DeploymentTarget.IIS)
                {
                    services.AddModule<WindowsIISDeploymentModule>();
                }
                else if (target == DeploymentTarget.IISSharedHosting)
                {
                    services.AddModule<WindowsIISSharedDeploymentModule>();
                    if (!string.IsNullOrEmpty(ftpHost))
                    {
                        services.AddModule<FtpUploadModule>();
                    }
                }
                else if (target == DeploymentTarget.LinuxService || target == DeploymentTarget.LinuxNginx || target == DeploymentTarget.LinuxCaddy)
                {
                    services.AddModule<LinuxSystemdDeploymentModule>();
                }
            });

        if (configurePipeline != null)
        {
            configurePipeline(builder);
        }

        var pipeline = await builder.ExecutePipelineAsync();
            
        Console.WriteLine("Deployment Pipeline Complete!");
    }
}
#endif
