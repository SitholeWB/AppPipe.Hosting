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
        var options = new DeploymentOptions();

        // Act
        var module = new WindowsServiceDeploymentModule(app, options);

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

    [Fact]
    public void WindowsIISSharedDeploymentModule_ShouldInitialize()
    {
        // Arrange
        var builder = AppPipeHostingApp.CreateBuilder(null!);
        var app = builder.Build();
        var options = new DeploymentOptions();

        // Act
        var module = new WindowsIISSharedDeploymentModule(app, options);

        // Assert
        Assert.NotNull(module);
    }

    [Fact]
    public void FtpUploadModule_ShouldInitialize()
    {
        // Arrange
        var options = new DeploymentOptions();

        // Act
        var module = new FtpUploadModule(options);

        // Assert
        Assert.NotNull(module);
    }
}
