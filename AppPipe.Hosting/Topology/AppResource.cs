namespace AppPipe.Hosting;

public abstract class AppResource
{
    public string Name { get; }
    public int AssignedPort { get; set; }
    public List<AppResource> References { get; } = new List<AppResource>();
    public Dictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();
    public List<AppResource> WaitDependencies { get; } = new List<AppResource>();

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

    protected AppResource(string name)
    {
        Name = name;
    }

    public AppResource WithReference(AppResource reference)
    {
        if (reference != null && !References.Contains(reference))
        {
            References.Add(reference);
        }
        return this;
    }

    public AppResource WithEndpoint(int port)
    {
        AssignedPort = port;
        return this;
    }

    public AppResource WithEnvironment(string name, string value)
    {
        EnvironmentVariables[name] = value;
        return this;
    }

    public AppResource WaitFor(AppResource dependency)
    {
        if (dependency != null && !WaitDependencies.Contains(dependency))
        {
            WaitDependencies.Add(dependency);
        }
        return this;
    }

    public AppResource WithAppPool(string appPoolName)
    {
        AppPoolName = appPoolName;
        return this;
    }

    public AppResource WithIISSite(string siteName)
    {
        IISSiteName = siteName;
        return this;
    }

    public AppResource WithServiceDisplayName(string displayName)
    {
        ServiceDisplayName = displayName;
        return this;
    }

    public AppResource WithServiceDescription(string description)
    {
        ServiceDescription = description;
        return this;
    }

    public AppResource WithServiceStartType(string startType)
    {
        ServiceStartType = startType;
        return this;
    }

    public AppResource WithServiceAccount(string account)
    {
        ServiceAccount = account;
        return this;
    }

    public AppResource WithHostingModel(string hostingModel)
    {
        HostingModel = hostingModel;
        return this;
    }

    public AppResource WithServicePassword(string password)
    {
        ServicePassword = password;
        return this;
    }

    public AppResource WithAppPath(string path)
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