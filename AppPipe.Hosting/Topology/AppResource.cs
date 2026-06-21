using System.Collections.Generic;

namespace AppPipe.Hosting;

public abstract class AppResource
{
    public string Name { get; }
    public int AssignedPort { get; set; }
    public List<AppResource> References { get; } = new List<AppResource>();
    public Dictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();
    public List<AppResource> WaitDependencies { get; } = new List<AppResource>();

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
}
