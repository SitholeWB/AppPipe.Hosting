#if !NETSTANDARD2_0
using System;
using System.Threading.Tasks;
using AppPipe.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModularPipelines.Host;
using ModularPipelines.Extensions;

namespace AppPipe.Hosting;

public class DeploymentOptions
{
    public string IISPath { get; set; } = string.Empty;
}

public class OnPremDeployer
{
    public static async Task CompileToOnPremAsync(AppPipeApp app, string iisPath = "")
    {
        Console.WriteLine("Starting AppPipe ModularPipelines Deployment...");
        
        var pipeline = await PipelineHostBuilder.Create()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(app);
                services.AddSingleton(new DeploymentOptions { IISPath = string.IsNullOrEmpty(iisPath) ? "" : (iisPath.StartsWith("/") ? iisPath : "/" + iisPath) });
                services.AddModule<PublishProjectsModule>();
                
                // Add conditional OS deployment modules
                if (OperatingSystem.IsWindows())
                {
                    services.AddModule<WindowsIISDeploymentModule>();
                }
                else if (OperatingSystem.IsLinux())
                {
                    services.AddModule<LinuxSystemdDeploymentModule>();
                }
            })
            .ExecutePipelineAsync();
            
        Console.WriteLine("Deployment Pipeline Complete!");
    }
}
#endif
