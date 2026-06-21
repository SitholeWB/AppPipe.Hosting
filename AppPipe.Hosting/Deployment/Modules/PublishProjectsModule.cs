using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AppPipe.Hosting;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Options;
using Microsoft.Extensions.Logging;

namespace AppPipe.Hosting;

public class PublishProjectsModule : Module<CommandResult[]>
{
    private readonly AppPipeApp _app;

    public PublishProjectsModule(AppPipeApp app)
    {
        _app = app;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();
        foreach (var resource in _app.Resources)
        {
            if (resource is AppPipe.Hosting.ProjectResource project)
            {
                var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", project.Name);
                
                context.Logger.LogInformation($"Publishing {project.Name} to {outputPath}...");
                
                var result = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("dotnet")
                {
                    Arguments = new[] { "publish", project.ProjectPath, "-c", "Release", "-o", outputPath }
                }, cancellationToken);
                
                results.Add(result);
            }
        }

        if (_app.HostProject != null)
        {
            var outputPath = Path.Combine(Environment.CurrentDirectory, "publish", _app.HostProject.Name);
            
            context.Logger.LogInformation($"Publishing Host Project {_app.HostProject.Name} to {outputPath}...");
            
            var result = await context.Command.ExecuteCommandLineTool(new CommandLineToolOptions("dotnet")
            {
                Arguments = new[] { "publish", _app.HostProject.ProjectPath, "-c", "Release", "-o", outputPath }
            }, cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
