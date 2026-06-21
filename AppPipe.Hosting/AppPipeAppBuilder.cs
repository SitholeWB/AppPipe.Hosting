namespace AppPipe.Hosting;

public class AppPipeAppBuilder
{
    private readonly List<AppResource> _resources = new List<AppResource>();

    public ProjectResource? HostProject { get; set; }

    public string[] Args { get; }

    public AppPipeAppBuilder(string[] args)
    {
        Args = args ?? Array.Empty<string>();
    }

    public ProjectResource AddProject(string name)
    {
        // Search up from AppContext.BaseDirectory to find the .sln or .slnx
        var currentDir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
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

        var resource = new ProjectResource(name, projectPath);
        _resources.Add(resource);
        return resource;
    }

    public ProjectResource AddProject(string name, string projectPath)
    {
        var resource = new ProjectResource(name, projectPath);
        _resources.Add(resource);
        return resource;
    }

    public ExecutableResource AddExecutable(string name, string command, string workingDirectory, params string[] args)
    {
        var resource = new ExecutableResource(name, command, workingDirectory, args);
        _resources.Add(resource);
        return resource;
    }

    public ExecutableResource AddNpmApp(string name, string workingDirectory, string scriptName = "dev")
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var command = isWindows ? "npm.cmd" : "npm";
        return AddExecutable(name, command, workingDirectory, "run", scriptName);
    }

    public Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? ConfigureGatewayAction { get; set; }

    public AppPipeAppBuilder ConfigureGateway(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder> configureAction)
    {
        ConfigureGatewayAction = configureAction;
        return this;
    }

    public AppPipeApp Build()
    {
        return new AppPipeApp(_resources, HostProject)
        {
            ConfigureGatewayAction = ConfigureGatewayAction
        };
    }
}

public class AppPipeApp
{
    public IReadOnlyList<AppResource> Resources { get; }
    public ProjectResource? HostProject { get; }
    public Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? ConfigureGatewayAction { get; set; }

    public AppPipeApp(IEnumerable<AppResource> resources, ProjectResource? hostProject = null)
    {
        Resources = new List<AppResource>(resources).AsReadOnly();
        HostProject = hostProject;
    }

    public static AppPipeAppBuilder CreateBuilder(string[] args)
    {
        return new AppPipeAppBuilder(args);
    }
}