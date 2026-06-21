using AppPipe.Hosting;

namespace AppPipe.DevHost;

// ----------------------------------------------------------------------------------------- SAMPLE
// APP: AppPipe.DevHost (The Orchestrator)
//
// This is the developer entry point. It defines the architecture (topology) of the application and
// boots all the microservices concurrently. It dynamically injects configuration so services can
// discover each other. -----------------------------------------------------------------------------------------

internal class Program
{
    private static async Task Main(string[] args)
    {
        // 1. Initialize the builder
        var builder = AppPipeApp.CreateBuilder(args);

        // Initialize the HostProject explicitly without adding it to the child projects list
        builder.HostProject = new AppPipe.Hosting.ProjectResource("AppPipe.DevHost", "");
        // We can't use AddProject("AppPipe.DevHost") because it adds it to the child project list
        // to be executed.

        // Configure the dashboard to explicitly use port 7001
        builder.HostProject.WithEndpoint(7001)
                           .WithEnvironment("LOG_LEVEL", "Debug");

        // Add the internal backend service
        //Or builder.AddProject<BackendWorker.Program>()
        var backend = builder.AddProject("BackendWorker")
                             .WithEndpoint(7002)
                             .WithEnvironment("LOG_LEVEL", "Debug");

        // Add the public frontend API and tell the orchestrator it depends on the backend
        var frontend = builder.AddProject("FrontendApi")
                              .WithReference(backend)
                              .WithEndpoint(7003)
                              .WithEnvironment("LOG_LEVEL", "Debug");

        // 3. Build the graph
        var app = builder.Build();

        if (args.Length > 0 && args[0] == "deploy")
        {
#if !NETSTANDARD2_0
            var deployPath = args.Length > 1 ? args[1] : "";
            await OnPremDeployer.CompileToOnPremAsync(app, deployPath);
#else
            Console.WriteLine("Deployment not supported on this framework.");
#endif
        }
        else if (Environment.GetEnvironmentVariable("APP_POOL_ID") != null)
        {
            // We are deployed inside IIS. Run the Dashboard/Gateway only.
            var gateway = new AppPipe.Hosting.GatewayHost();
            await gateway.StartAsync(null, app);
            await Task.Delay(-1);
        }
        else
        {
            var runner = new DevHostRunner(app);
            await runner.RunAsync();
        }
    }
}