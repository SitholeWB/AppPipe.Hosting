using AppPipe.Hosting;

namespace AppPipe.DevHost;

// ----------------------------------------------------------------------------------------- SAMPLE
// APP: AppPipe.DevHost (The Orchestrator) // This is the developer entry point. It defines the
// architecture (topology) of the application and boots all the microservices concurrently. It
// dynamically injects configuration so services can discover each other. -----------------------------------------------------------------------------------------

internal class Program
{
    private static async Task Main(string[] args)
    {
        // 1. Initialize the builder
        var builder = AppPipeHostingApp.CreateBuilder(args);

        // Initialize the HostProject explicitly without adding it to the child projects list
        builder.HostProject = new AppPipeHostingProjectResource("AppPipe.DevHost", "");
        // We can't use AddProject("AppPipe.DevHost") because it adds it to the child project list
        // to be executed.

        // Configure the dashboard to explicitly use port 7001
        builder.HostProject.WithEndpoint(7001)
                           .WithAppPath("/AppPipe.DevHost")
                           .WithEnvironment("LOG_LEVEL", "Debug");

        // Add the internal backend service using compile-safe project names
        var backend = builder.AddProject(AppPipeProjects.BackendWorker)
                             .WithEndpoint(7002)
                             .WithEnvironment("LOG_LEVEL", "Debug")
                             .WithAppPool("CustomBackendPool")
                             .WithIISSite("Default Web Site")
                             .WithAppPath("/BackendWorker")
                             .WithServiceDisplayName("AppPipe Backend Worker Service")
                             .WithServiceDescription("AppPipe backend processing service runs tasks and telemetries.")
                             .WithServiceStartType("auto");

        // Add the public frontend API and tell the orchestrator it depends on the backend
        var frontend = builder.AddProject(AppPipeProjects.FrontendApi)
                              .WithReference(backend)
                              .WithEndpoint(7003)
                              .WithEnvironment("LOG_LEVEL", "Debug")
                              .WithAppPool("CustomFrontendPool")
                              .WithIISSite("Default Web Site")
                              .WithAppPath("/FrontendApi")
                              .WithServiceDisplayName("AppPipe Frontend API Service")
                              .WithServiceDescription("AppPipe public API gateway and web request handler.")
                              .WithServiceStartType("auto");

        // 3. Build the graph
        var app = builder.Build();

        if (args.Length > 0 && (args[0] == "deploy" || args[0] == "deploy-service" || args[0] == "--deploy"))
        {
#if !NETSTANDARD2_0
            var target = DeploymentTarget.IIS;
            var deployPath = "";

            if (args[0] == "deploy-service")
            {
                target = DeploymentTarget.WindowsService;
                deployPath = args.Length > 1 ? args[1] : "";
            }
            else if (args[0] == "--deploy")
            {
                var targetStr = args.Length > 1 ? args[1] : "iis";
                deployPath = args.Length > 2 ? args[2] : "";

                if (targetStr.Equals("windows-service", StringComparison.OrdinalIgnoreCase) || targetStr.Equals("service", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.WindowsService;
                }
                else if (targetStr.Equals("iis", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.IIS;
                }
                else if (targetStr.Equals("linux-service", StringComparison.OrdinalIgnoreCase) || targetStr.Equals("systemd", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.LinuxService;
                }
                else if (targetStr.Equals("linux-nginx", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.LinuxNginx;
                }
                else if (targetStr.Equals("linux-caddy", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.LinuxCaddy;
                }
                else if (targetStr.Equals("iis-shared", StringComparison.OrdinalIgnoreCase) || targetStr.Equals("shared", StringComparison.OrdinalIgnoreCase))
                {
                    target = DeploymentTarget.IISSharedHosting;
                }
                else
                {
                    throw new ArgumentException($"Unknown deployment target: {targetStr}");
                }
            }
            else // "deploy"
            {
                target = DeploymentTarget.IIS;
                deployPath = args.Length > 1 ? args[1] : "";
            }

            await OnPremDeployer.CompileToOnPremAsync(app, target, deployPath);
#else
            Console.WriteLine("Deployment not supported on this framework.");
#endif
        }
        else if (Environment.GetEnvironmentVariable("APP_POOL_ID") != null ||
                 Environment.GetEnvironmentVariable("WINDOWS_SERVICE") == "true" ||
                 Environment.GetEnvironmentVariable("LINUX_SERVICE") == "true")
        {
            // We are deployed inside IIS, Windows Service, or Linux systemd Service. Run the
            // Dashboard/Gateway only.
            var gateway = new AppPipe.Hosting.GatewayAppPipeHost();
            await gateway.StartAsync(string.Empty, app, app.ConfigureGatewayAction);
            await Task.Delay(-1);
        }
        else
        {
            var runner = new AppPipeDevHostRunner(app);
            await runner.RunAsync();
        }
    }
}