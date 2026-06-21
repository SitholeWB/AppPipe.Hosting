namespace AppPipe.Hosting;

public class ExecutableAppPipeHostingResource : AppPipeHostingResource
{
    public string Command { get; }
    public string WorkingDirectory { get; }
    public string[] Args { get; }

    public ExecutableAppPipeHostingResource(string name, string command, string workingDirectory, params string[] args) : base(name)
    {
        Command = command;
        WorkingDirectory = workingDirectory;
        Args = args ?? System.Array.Empty<string>();
    }
}
