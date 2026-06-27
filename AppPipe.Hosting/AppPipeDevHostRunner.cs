using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AppPipe.Hosting;

public class AppPipeDevHostRunner
{
    private readonly AppPipeHostingApp _app;
    private readonly List<Process> _childProcesses = [];
    private WindowsJobObject? _jobObject;

    public AppPipeDevHostRunner(AppPipeHostingApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    public async Task RunAsync()
    {
        Console.WriteLine("AppPipe.NET DevHost Starting...");

        // Ensure all child processes are terminated when this host exits.
        // ProcessExit covers graceful exits (normal stop, Ctrl+C via VS, unhandled exceptions).
        // CancelKeyPress covers Ctrl+C from a terminal.
        // On Windows, the Job Object additionally covers hard kills (TerminateProcess from IDE/Task Manager).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };

        if (OperatingSystem.IsWindows())
            _jobObject = new WindowsJobObject();

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

        var yarpConfigFile = Path.GetFullPath("yarp.json");
        await File.WriteAllTextAsync(yarpConfigFile, yarpConfig.ToString());

        // 2. Start Gateway Internally
        var gatewayHost = new GatewayAppPipeHost();
        var ports = await gatewayHost.StartAsync(yarpConfigFile, _app, _app.ConfigureGatewayAction);
        var pathBase = _app.HostProject?.AppPath ?? "";
        if (pathBase == "/") pathBase = "";
        Console.WriteLine($"AppPipe Gateway (Dashboard & Proxy) started on http://localhost:{ports.DashboardPort}");
        Console.WriteLine($"-> Dashboard: http://localhost:{ports.DashboardPort}{pathBase}/dashboard");
        Console.WriteLine($"-> Telemetry: http://localhost:{ports.TelemetryPort}");

        // 3. Start Child Projects
        var tasks = new List<Task>();
        foreach (var resource in _app.Resources)
        {
            tasks.Add(StartResourceAsync(resource, ports.TelemetryPort));
        }

        await Task.WhenAll(tasks);
        await gatewayHost.StopAsync();

        if (OperatingSystem.IsWindows())
            _jobObject?.Dispose();
    }

    private void Shutdown()
    {
        foreach (var process in _childProcesses)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* process may have already exited */ }
        }
    }

    private async Task StartResourceAsync(AppPipeHostingResource resource, int gatewayPort)
    {
        foreach (var dep in resource.WaitDependencies)
        {
            Console.WriteLine($"[{resource.Name}] Waiting for {dep.Name} to be ready on port {dep.AssignedPort}...");
            await WaitForPortAsync(dep.AssignedPort);
        }

        var envVars = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = $"http://localhost:{resource.AssignedPort}",
            ["PORT"] = resource.AssignedPort.ToString(),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://localhost:{gatewayPort}"
        };

        foreach (var reference in resource.References)
            envVars[$"services__{reference.Name}__http__0"] = $"http://localhost:{reference.AssignedPort}";

        foreach (var env in resource.EnvironmentVariables)
            envVars[env.Key] = env.Value;

        Console.WriteLine($"Starting {resource.Name} on port {resource.AssignedPort}...");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (resource is AppPipeHostingProjectResource proj)
        {
            startInfo.FileName = "dotnet";
            startInfo.Arguments = $"run --no-launch-profile --project {proj.ProjectPath}";
            startInfo.WorkingDirectory = Path.GetDirectoryName(proj.ProjectPath);
        }
        else if (resource is ExecutableAppPipeHostingResource exec)
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
            startInfo.EnvironmentVariables[env.Key] = env.Value;

        // Prevent IDE-injected startup assemblies from crashing child processes
        startInfo.EnvironmentVariables.Remove("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES");

        var process = new Process { StartInfo = startInfo };
        _childProcesses.Add(process);

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[{resource.Name}] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[{resource.Name} ERR] {e.Data}");
        };

        process.Start();

        if (OperatingSystem.IsWindows())
            _jobObject?.Add(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Server.LingerState = new LingerOption(true, 0);
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }
}