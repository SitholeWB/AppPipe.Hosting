namespace AppPipe.Hosting;

public class AppPipeHostingAppBuilder
{
    private readonly List<AppPipeHostingResource> _resources = new List<AppPipeHostingResource>();

    public AppPipeHostingProjectResource? HostProject { get; set; }

    public string[] Args { get; }

    public AppPipeHostingAppBuilder(string[] args)
    {
        Args = args ?? Array.Empty<string>();
    }

    public AppPipeHostingProjectResource AddProject(string name)
    {
        // Search up from AppContext.BaseDirectory to find the .sln or .slnx
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        string projectPath = string.Empty;

        while (currentDir != null)
        {
            if (currentDir.GetFiles("*.sln").Length > 0 || currentDir.GetFiles("*.slnx").Length > 0 || currentDir.GetDirectories("samples").Length > 0)
            {
                // Found solution root. Search for projectName.csproj
                var files = currentDir.GetFiles($"{name}.csproj", System.IO.SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    projectPath = files[0].FullName;
                    break;
                }
            }
            currentDir = currentDir.Parent;
        }

        if (string.IsNullOrEmpty(projectPath))
        {
            throw new Exception($"Could not find project file for {name}");
        }

        var resource = new AppPipeHostingProjectResource(name, projectPath);
        _resources.Add(resource);
        return resource;
    }

    public AppPipeHostingProjectResource AddProject(string name, string projectPath)
    {
        var resource = new AppPipeHostingProjectResource(name, projectPath);
        _resources.Add(resource);
        return resource;
    }

    public ExecutableAppPipeHostingResource AddExecutable(string name, string command, string workingDirectory, params string[] args)
    {
        var resource = new ExecutableAppPipeHostingResource(name, command, workingDirectory, args);
        _resources.Add(resource);
        return resource;
    }

    public ExecutableAppPipeHostingResource AddNpmApp(string name, string workingDirectory, string scriptName = "dev")
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var command = isWindows ? "npm.cmd" : "npm";
        return AddExecutable(name, command, workingDirectory, "run", scriptName);
    }

    public Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? ConfigureGatewayAction { get; set; }

    public AppPipeHostingAppBuilder ConfigureGateway(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder> configureAction)
    {
        ConfigureGatewayAction = configureAction;
        return this;
    }

    public AppPipeHostingApp Build()
    {
        return new AppPipeHostingApp(_resources, HostProject)
        {
            ConfigureGatewayAction = ConfigureGatewayAction
        };
    }
}

public class AppPipeHostingApp
{
    public IReadOnlyList<AppPipeHostingResource> Resources { get; }
    public AppPipeHostingProjectResource? HostProject { get; }
    public Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? ConfigureGatewayAction { get; set; }

    public AppPipeHostingApp(IEnumerable<AppPipeHostingResource> resources, AppPipeHostingProjectResource? hostProject = null)
    {
        Resources = new List<AppPipeHostingResource>(resources).AsReadOnly();
        HostProject = hostProject;
    }

    public static AppPipeHostingAppBuilder CreateBuilder(string[] args)
    {
        return new AppPipeHostingAppBuilder(args);
    }
}