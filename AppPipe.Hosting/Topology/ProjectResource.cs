using System.Collections.Generic;

namespace AppPipe.Hosting;

public class ProjectResource : AppResource
{
    public string ProjectPath { get; }

    public ProjectResource(string name, string projectPath) : base(name)
    {
        ProjectPath = projectPath;
    }
}
