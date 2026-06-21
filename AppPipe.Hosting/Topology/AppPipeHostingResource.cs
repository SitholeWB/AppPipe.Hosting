namespace AppPipe.Hosting;

public abstract class AppPipeHostingResource
{
    public string Name { get; }
    public int AssignedPort { get; set; }
    public List<AppPipeHostingResource> References { get; } = new List<AppPipeHostingResource>();
    public Dictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();
    public List<AppPipeHostingResource> WaitDependencies { get; } = new List<AppPipeHostingResource>();

    // Customizable Deployment Configurations
    public string? AppPoolName { get; set; }

    public string IISSiteName { get; set; } = "Default Web Site";
    public string? AppPath { get; set; }
    public string? ServiceDisplayName { get; set; }
    public string? ServiceDescription { get; set; }
    public string ServiceStartType { get; set; } = "auto";
    public string? ServiceAccount { get; set; }
    public string? ServicePassword { get; set; }
    public string? HostingModel { get; set; }

    protected AppPipeHostingResource(string name)
    {
        Name = name;
    }

    public AppPipeHostingResource WithReference(AppPipeHostingResource reference)
    {
        if (reference != null && !References.Contains(reference))
        {
            References.Add(reference);
        }
        return this;
    }

    public AppPipeHostingResource WithEndpoint(int port)
    {
        AssignedPort = port;
        return this;
    }

    public AppPipeHostingResource WithEnvironment(string name, string value)
    {
        EnvironmentVariables[name] = value;
        return this;
    }

    public AppPipeHostingResource WaitFor(AppPipeHostingResource dependency)
    {
        if (dependency != null && !WaitDependencies.Contains(dependency))
        {
            WaitDependencies.Add(dependency);
        }
        return this;
    }

    public AppPipeHostingResource WithAppPool(string appPoolName)
    {
        AppPoolName = appPoolName;
        return this;
    }

    public AppPipeHostingResource WithIISSite(string siteName)
    {
        IISSiteName = siteName;
        return this;
    }

    public AppPipeHostingResource WithServiceDisplayName(string displayName)
    {
        ServiceDisplayName = displayName;
        return this;
    }

    public AppPipeHostingResource WithServiceDescription(string description)
    {
        ServiceDescription = description;
        return this;
    }

    public AppPipeHostingResource WithServiceStartType(string startType)
    {
        ServiceStartType = startType;
        return this;
    }

    public AppPipeHostingResource WithServiceAccount(string account)
    {
        ServiceAccount = account;
        return this;
    }

    public AppPipeHostingResource WithHostingModel(string hostingModel)
    {
        HostingModel = hostingModel;
        return this;
    }

    public AppPipeHostingResource WithServicePassword(string password)
    {
        ServicePassword = password;
        return this;
    }

    public AppPipeHostingResource WithAppPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            AppPath = "/";
        }
        else
        {
            AppPath = path.StartsWith("/") ? path : "/" + path;
        }
        return this;
    }
}