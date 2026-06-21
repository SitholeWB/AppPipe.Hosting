#if !NETSTANDARD2_0
using System;
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
    LinuxCaddy
}

public class DeploymentOptions
{
    public DeploymentTarget Target { get; set; } = DeploymentTarget.IIS;
    public string Path { get; set; } = string.Empty;
    public string IISPath => Path;
}

public class OnPremDeployer
{
    public static async Task CompileToOnPremAsync(AppPipeHostingApp app, DeploymentTarget target = DeploymentTarget.IIS, string path = "")
    {
        Console.WriteLine($"Starting AppPipe ModularPipelines Deployment targeting {target}...");
        
        var pipeline = await PipelineHostBuilder.Create()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(app);
                services.AddSingleton(new DeploymentOptions 
                { 
                    Target = target,
                    Path = target == DeploymentTarget.IIS
                        ? (string.IsNullOrEmpty(path) ? "" : (path.StartsWith("/") ? path : "/" + path))
                        : path
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
                else if (target == DeploymentTarget.LinuxService || target == DeploymentTarget.LinuxNginx || target == DeploymentTarget.LinuxCaddy)
                {
                    services.AddModule<LinuxSystemdDeploymentModule>();
                }
            })
            .ExecutePipelineAsync();
            
        Console.WriteLine("Deployment Pipeline Complete!");
    }
}
#endif
