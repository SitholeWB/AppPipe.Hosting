namespace AppPipe.Hosting;

public class AppPipeHostingProjectResource : AppPipeHostingResource
{
    public string ProjectPath { get; }

    public AppPipeHostingProjectResource(string name, string projectPath) : base(name)
    {
        ProjectPath = projectPath;
    }
}