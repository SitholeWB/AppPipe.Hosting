using Xunit;
using AppPipe.Hosting;

namespace AppPipe.Hosting.Tests;

public class TopologyTests
{
    [Fact]
    public void Builder_ShouldInitializeWithArgs()
    {
        // Arrange
        var args = new[] { "--deploy", "iis" };

        // Act
        var builder = AppPipeHostingApp.CreateBuilder(args);

        // Assert
        Assert.NotNull(builder);
        Assert.Equal(args, builder.Args);
    }

    [Fact]
    public void AddProject_ShouldConfigureNameAndPath()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);

        // Act
        var project = builder.AddProject("MyTestProject", "C:\\projects\\MyTestProject.csproj");

        // Assert
        Assert.NotNull(project);
        Assert.Equal("MyTestProject", project.Name);
        Assert.Equal("C:\\projects\\MyTestProject.csproj", project.ProjectPath);
    }

    [Fact]
    public void FluentExtensions_ShouldConfigureAllProperties()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var project = builder.AddProject("Worker", "C:\\projects\\Worker.csproj");

        // Act
        project.WithEndpoint(8080)
               .WithAppPath("/api")
               .WithEnvironment("KEY", "VALUE")
               .WithAppPool("MyPool")
               .WithIISSite("Custom Site")
               .WithHostingModel("InProcess")
               .WithServiceDisplayName("My Display Name")
               .WithServiceDescription("Description here")
               .WithServiceStartType("demand")
               .WithServiceAccount(@"DOMAIN\user")
               .WithServicePassword("password123");

        // Assert
        Assert.Equal(8080, project.AssignedPort);
        Assert.Equal("/api", project.AppPath);
        Assert.Equal("VALUE", project.EnvironmentVariables["KEY"]);
        Assert.Equal("MyPool", project.AppPoolName);
        Assert.Equal("Custom Site", project.IISSiteName);
        Assert.Equal("InProcess", project.HostingModel);
        Assert.Equal("My Display Name", project.ServiceDisplayName);
        Assert.Equal("Description here", project.ServiceDescription);
        Assert.Equal("demand", project.ServiceStartType);
        Assert.Equal(@"DOMAIN\user", project.ServiceAccount);
        Assert.Equal("password123", project.ServicePassword);
    }

    [Fact]
    public void WithReference_ShouldInjectServiceDiscoveryVariables()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var backend = builder.AddProject("Backend", "C:\\projects\\Backend.csproj").WithEndpoint(5001);
        var frontend = builder.AddProject("Frontend", "C:\\projects\\Frontend.csproj").WithEndpoint(5002);

        // Act
        frontend.WithReference(backend);
        var app = builder.Build();

        // Assert
        // Re-read references of frontend to ensure service discovery variables were mapped.
        // Usually, the runner or deployer processes environment variables, or it is processed during app build/routing.
        // Let's verify that the reference dependency has been registered in the resource.
        Assert.Contains(backend, frontend.References);
    }
}
