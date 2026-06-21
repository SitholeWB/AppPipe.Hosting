using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AppPipe.Hosting;

public class DevHostRunner
{
    private readonly AppPipeApp _app;

    public DevHostRunner(AppPipeApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    public async Task RunAsync()
    {
        Console.WriteLine("AppPipe.NET DevHost Starting...");

        // 1. Generate YARP Config
        var yarpConfig = new System.Text.StringBuilder();
        yarpConfig.AppendLine("{ \"ReverseProxy\": { \"Routes\": {");
        var routes = new List<string>();
        var clusters = new List<string>();

        foreach (var p in _app.Resources)
        {
            if (p.AssignedPort == 0)
            {
                p.AssignedPort = GetFreePort();
                Console.WriteLine($"Allocated port {p.AssignedPort} for {p.Name}");
            }
            routes.Add($"\"{p.Name}Route\": {{ \"ClusterId\": \"{p.Name}Cluster\", \"Match\": {{ \"Path\": \"/{p.Name.ToLower()}/{{**catch-all}}\" }} }}");
            clusters.Add($"\"{p.Name}Cluster\": {{ \"Destinations\": {{ \"destination1\": {{ \"Address\": \"http://localhost:{p.AssignedPort}/\" }} }} }}");
        }
        yarpConfig.AppendLine(string.Join(",", routes));
        yarpConfig.AppendLine("}, \"Clusters\": {");
        yarpConfig.AppendLine(string.Join(",", clusters));
        yarpConfig.AppendLine("} } }");

        var yarpConfigFile = System.IO.Path.GetFullPath("yarp.json");
        System.IO.File.WriteAllText(yarpConfigFile, yarpConfig.ToString());

        // 2. Start Gateway Internally
        var gatewayHost = new GatewayHost();
        var ports = await gatewayHost.StartAsync(yarpConfigFile, _app, _app.ConfigureGatewayAction);
        Console.WriteLine($"AppPipe Gateway (Dashboard & Proxy) started on http://localhost:{ports.DashboardPort}");
        Console.WriteLine($"-> Dashboard: http://localhost:{ports.DashboardPort}/dashboard");
        Console.WriteLine($"-> Telemetry: http://localhost:{ports.TelemetryPort}");

        // 3. Start Child Projects
        var tasks = new List<Task>();
        foreach (var resource in _app.Resources)
        {
            tasks.Add(StartResourceAsync(resource, ports.TelemetryPort));
        }

        // Wait for all to exit (which usually means forever, or until developer stops it)
        await Task.WhenAll(tasks);
        await gatewayHost.StopAsync();
    }

    private async Task StartResourceAsync(AppResource resource, int gatewayPort)
    {
        foreach (var dep in resource.WaitDependencies)
        {
            Console.WriteLine($"[{resource.Name}] Waiting for {dep.Name} to be ready on port {dep.AssignedPort}...");
            await WaitForPortAsync(dep.AssignedPort);
        }

        var envVars = new Dictionary<string, string>();

        // Inject loopback ports
        envVars["ASPNETCORE_URLS"] = $"http://localhost:{resource.AssignedPort}";
        envVars["PORT"] = resource.AssignedPort.ToString(); // For Node.js/Executables

        // Inject reference service discovery
        foreach (var reference in resource.References)
        {
            envVars[$"services__{reference.Name}__http__0"] = $"http://localhost:{reference.AssignedPort}";
        }

        // Inject OTel to internal Gateway Telemetry Port
        envVars["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://localhost:{gatewayPort}";

        // Inject custom environment variables
        foreach (var env in resource.EnvironmentVariables)
        {
            envVars[env.Key] = env.Value;
        }

        Console.WriteLine($"Starting {resource.Name} on port {resource.AssignedPort}...");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (resource is ProjectResource proj)
        {
            startInfo.FileName = "dotnet";
            startInfo.Arguments = $"run --no-launch-profile --project {proj.ProjectPath}";
            startInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(proj.ProjectPath);
        }
        else if (resource is ExecutableResource exec)
        {
            startInfo.FileName = exec.Command;
            startInfo.Arguments = string.Join(" ", exec.Args);
            startInfo.WorkingDirectory = exec.WorkingDirectory;
        }
        else
        {
            Console.WriteLine($"Unknown resource type for {resource.Name}");
            return;
        }

        foreach (var env in envVars)
        {
            startInfo.EnvironmentVariables[env.Key] = env.Value;
        }

        // Prevent Visual Studio / IDE injected hosting startup assemblies from crashing child processes
        startInfo.EnvironmentVariables.Remove("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES");

        var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[{resource.Name}] {e.Data}");
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[{resource.Name} ERR] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
    }

    private int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task WaitForPortAsync(int port)
    {
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                break;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }
}