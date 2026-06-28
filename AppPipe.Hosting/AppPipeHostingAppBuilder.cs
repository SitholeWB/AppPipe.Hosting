using System.Linq;

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

    // New: choose package manager
    public ExecutableAppPipeHostingResource AddFrontendApp(string name, string workingDirectory, PackageManager manager, string? scriptName = null)
    {
        // If scriptName is null, try to detect common defaults
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            var detected = FrontendCommandResolver.DetectDefaultScript(workingDirectory, "dev", "start", "serve");
            scriptName = detected ?? "dev";
        }

        var (exe, args) = FrontendCommandResolver.Resolve(manager, scriptName);
        return AddExecutable(name, exe, workingDirectory, args);
    }

    // New: custom command (full executable + args)
    public ExecutableAppPipeHostingResource AddFrontendApp(string name, string workingDirectory, string customCommand, params string[] args)
    {
        // If customCommand contains spaces, treat first token as executable
        var tokens = TokenizeCommand(customCommand);
        var exe = tokens.Length > 0 ? tokens[0] : customCommand;
        var initialArgs = tokens.Length > 1 ? tokens.Skip(1).ToArray() : System.Array.Empty<string>();
        var finalArgs = initialArgs.Concat(args ?? System.Array.Empty<string>()).ToArray();
        return AddExecutable(name, exe, workingDirectory, finalArgs);
    }

    private string[] TokenizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return System.Array.Empty<string>();

        var list = new List<string>();
        var currentToken = new System.Text.StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (currentToken.Length > 0)
                    {
                        list.Add(currentToken.ToString());
                        currentToken.Clear();
                    }
                }
                else
                {
                    currentToken.Append(c);
                }
            }
        }

        if (currentToken.Length > 0)
        {
            list.Add(currentToken.ToString());
        }

        return list.ToArray();
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

public enum PackageManager
{
    Npm,
    Yarn,
    Pnpm,
    Bun
}

public static class FrontendCommandResolver
{
    public static string? DetectDefaultScript(string workingDirectory, params string[] candidates)
    {
        try
        {
            var packageJsonPath = System.IO.Path.Combine(workingDirectory, "package.json");
            if (!System.IO.File.Exists(packageJsonPath)) return null;

            var content = System.IO.File.ReadAllText(packageJsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("scripts", out var scriptsElement))
            {
                foreach (var candidate in candidates)
                {
                    if (scriptsElement.TryGetProperty(candidate, out _))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // Ignored - fallback to default candidate
        }
        return null;
    }

    public static (string Command, string[] Args) Resolve(PackageManager manager, string scriptName)
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        string cmd;
        string[] args;

        switch (manager)
        {
            case PackageManager.Yarn:
                cmd = isWindows ? "yarn.cmd" : "yarn";
                args = new[] { "run", scriptName };
                break;
            case PackageManager.Pnpm:
                cmd = isWindows ? "pnpm.cmd" : "pnpm";
                args = new[] { "run", scriptName };
                break;
            case PackageManager.Bun:
                cmd = "bun";
                args = new[] { "run", scriptName };
                break;
            case PackageManager.Npm:
            default:
                cmd = isWindows ? "npm.cmd" : "npm";
                args = new[] { "run", scriptName };
                break;
        }

        return (cmd, args);
    }
}