using Xunit;
using System.Runtime.InteropServices;
using AppPipe.Hosting;

namespace AppPipe.Hosting.Tests;

public class DeploymentModuleTests
{
    [Fact]
    public void WindowsServiceDeploymentModule_ShouldInitialize()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var app = builder.Build();

        // Act
        var module = new WindowsServiceDeploymentModule(app);

        // Assert
        Assert.NotNull(module);
    }

    [Fact]
    public void WindowsIISDeploymentModule_ShouldInitialize()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var app = builder.Build();
        var options = new DeploymentOptions();

        // Act
        var module = new WindowsIISDeploymentModule(app, options);

        // Assert
        Assert.NotNull(module);
    }

    [Fact]
    public void LinuxSystemdDeploymentModule_ShouldInitialize()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var app = builder.Build();
        var options = new DeploymentOptions();

        // Act
        var module = new LinuxSystemdDeploymentModule(app, options);

        // Assert
        Assert.NotNull(module);
    }
}
