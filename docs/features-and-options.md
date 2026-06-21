# AppPipe.Hosting: Complete Features & Configuration Reference Guide

This reference guide provides an in-depth explanation of all features, topology options, configuration settings, deployment internals, and CI/CD pipelines available in **AppPipe.Hosting**.

---

## ⚙️ Core Features & Capabilities

```mermaid
graph TD
    Client(Browser/Client) -->|HTTP Requests| YarpProxy[YARP Reverse Proxy]
    
    subgraph AppPipe.Hosting Core
        YarpProxy -->|Route matches /service/*| ChildApp[Microservice Process]
        OTLP[OTLP Telemetry Collector] -->|Parses Logs, Traces, Metrics| TelemetryStore[(InMemoryTelemetryStore)]
        TelemetryStore --> Dashboard[Blazor Dashboard UI]
    end
    
    ChildApp -->|Exports Logs/Traces/Metrics| OTLP
```

### 1. YARP-Based Routing & Service Discovery
AppPipe integrates **YARP (Yet Another Reverse Proxy)** to act as the central entry point for all client requests.
* **Catch-all Routing**: When you register a service named `BackendWorker`, AppPipe automatically maps the path `/backendworker/{**catch-all}` to proxy traffic to the assigned port of the backend.
* **Environment Injection**: When a service has a reference to another (declared via `.WithReference(dependency)`), AppPipe injects service discovery environment variables in the format:
  `services__<Name>__http__0` = `http://localhost:<Port>/` (for local runs) or the relative virtual directory path (for IIS).

### 2. OTLP Telemetry Collector
AppPipe exposes a local HTTP/2 Kestrel endpoint that acts as a fully compliant **OpenTelemetry (OTLP) Collector** supporting gRPC exports.
* **Zero Configuration**: Microservices configure standard .NET OTLP exporters, which automatically detect and output telemetry to this local gateway.
* **Structured Logs & Traces**: Captures structured console logs, distributed tracing spans, and resource metrics.

### 3. InMemory Telemetry Store
By default, telemetry is stored in an in-memory database (`InMemoryTelemetryStore`).
* **Circular Limit**: To prevent memory leaks on on-premises hosting VMs, the in-memory store retains a max buffer of **200 traces, logs, and metrics** using a FIFO queue.
* **Extensible Storage**: Developers can override this default store to persist telemetry to databases like SQLite, PostgreSQL, SQL Server, or ClickHouse by implementing the `ITelemetryStore` interface.

### 4. Blazor Dashboard UI
A visual dashboard that allows real-time diagnostics:
* **Waterfalls**: Flamegraphs showing tracing cascades across services.
* **Console Viewer**: Live, searchable stream of logs.
* **Metric Graphs**: Visual charts plotting memory, CPU, and custom metrics.
* **Render Modes**:
  * **InteractiveServer**: Uses WebSockets to stream telemetry updates in real-time.
  * **Static SSR**: Disables WebSockets, rendering pure base-relative HTML pages. Essential for resource-constrained production VMs and smooth operations behind IIS reverse proxies.

---

## ⚙️ AppResource Fluent Topology Options

### Project Registration Methods

AppPipe supports two ways to register your microservices in the topology builder:

#### 1. Compile-Safe Project Registration (Recommended)
You can register microservices using strongly-typed string constants generated automatically at build time. 

* **How it works**: You add a standard `<ProjectReference>` to your microservice in the orchestrator's `.csproj` file:
  ```xml
  <ProjectReference Include="..\BackendWorker\BackendWorker.csproj" />
  ```
  AppPipe's built-in target automatically intercepts this and injects:
  - `ReferenceOutputAssembly="false"` (prevents compiling/linking the microservice's assembly).
  - `SkipGetTargetFrameworkProperties="true"` (prevents cross-framework target errors).
  - `Private="false"` (prevents MSBuild from copying any assemblies or content files like `appsettings.json` to the orchestrator's publish folder, avoiding `NETSDK1152` duplicate conflicts).
  
  > [!NOTE]
  > **Referencing Shared Code or Extension Libraries:**
  > If the orchestrator project references a helper library or a shared extension library (e.g. adding extensions to `AppPipeAppBuilder`) that it needs to consume code from, you can disable the automatic decoupling by setting the `AppProject="false"` metadata on the reference:
  > ```xml
  > <ProjectReference Include="..\MySharedLibrary\MySharedLibrary.csproj" AppProject="false" />
  > ```
  
  At build time, AppPipe automatically generates a helper class `Projects` containing project names:
  ```csharp
  namespace AppPipe.Hosting
  {
      public static class Projects
      {
          public const string BackendWorker = "BackendWorker";
          public const string FrontendApi = "FrontendApi";
      }
  }
  ```
  You can then register the project in `Program.cs` like this:
  ```csharp
  var backend = builder.AddProject(Projects.BackendWorker);
  ```
* **Pros**: Refactoring-safe and compile-time validated. If a project is renamed or removed, you will get a compile-time build error. There are no runtime loading dependencies or file copying issues.
* **Cons**: Requires adding decoupled `<ProjectReference>` configurations in the orchestrator project file.

#### 2. Raw String-Based Registration (Fully Decoupled)
If you prefer not to add `<ProjectReference>` items to the orchestrator project file, you can register projects directly using raw string names:
```csharp
var backend = builder.AddProject("BackendWorker");
```
* **How it works**: Searches upwards from `AppContext.BaseDirectory` for the `.sln` or `.slnx` file, and recursively finds the matching `{ProjectName}.csproj` file.
* **Pros**: Complete compilation decoupling. The orchestrator project (`AppPipe.DevHost`) does not need to know about or reference the microservice projects in its `.csproj`.
* **Cons**: No compile-time validation. If you rename the project on disk, you must update the string manually.

---

### API Reference Table


| Fluent Method | Argument Type | Description |
| :--- | :--- | :--- |
| `.WithEndpoint(port)` | `int` | Explicitly binds the application port. If omitted, a free port is dynamically allocated. |
| `.WithEnvironment(key, val)`| `string, string` | Injects custom environment variables into the process. |
| `.WithReference(dep)` | `AppResource` | Sets up service discovery and links the current project to the dependency. |
| `.WaitFor(dep)` | `AppResource` | Delays startup of the resource until the dependency's port is active. |
| `.WithAppPool(name)` | `string` | Sets the custom IIS Application Pool name for this service. |
| `.WithIISSite(siteName)` | `string` | Sets the target IIS Site (defaults to `"Default Web Site"`). |
| `.WithAppPath(path)` | `string` | Sets the custom virtual application path. For Windows IIS, this maps to the sub-application virtual path under the site (e.g. `"/api"`). For Linux Nginx and Caddy reverse proxies, this determines the location routing block path (e.g. `location /api/`). Empty string `""` or `"/"` deploys the app directly as the root application (`/`) of the site/proxy. |
| `.WithHostingModel(model)` | `string` | Sets the IIS hosting model (`"InProcess"` or `"OutOfProcess"`). |
| `.WithServiceDisplayName(name)`| `string` | Sets the Windows Service Manager (SCM) Display Name. |
| `.WithServiceDescription(desc)`| `string` | Sets the SCM / systemd service description. |
| `.WithServiceStartType(type)` | `string` | SCM startup trigger (`"auto"`, `"demand"`, or `"disabled"`). |
| `.WithServiceAccount(account)` | `string` | SCM/AppPool user identity (e.g. `LocalSystem`, `NetworkService`, or domain accounts like `DOMAIN\user`). |
| `.WithServicePassword(pwd)` | `string` | Password matching the custom domain/local user identity. |

---

## 💻 Full End-to-End Orchestrator Example (`Program.cs`)

Below is a complete, production-ready `Program.cs` orchestrator topology showing configuration binding, dashboard customization, and fluent resource definition:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AppPipe.Hosting;

namespace AppPipe.DevHost;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // 1. Initialize .NET Configuration Builder
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")}.json", optional: true)
            .AddEnvironmentVariables(prefix: "APPIPE__") // e.g. APPIPE__BackendWorker__ServicePassword
            .AddCommandLine(args)
            .Build();

        // 2. Initialize the AppPipe App Builder
        var builder = AppPipeApp.CreateBuilder(args);

        // 3. Customize and configure the Dashboard itself (HostProject)
        var dashboardName = config["Dashboard:Name"] ?? "AppPipeDashboard";
        builder.HostProject = new ProjectResource(dashboardName, "")
            .WithEndpoint(7001)
            .WithIISSite(config["Dashboard:IISSiteName"] ?? "Default Web Site")
            .WithAppPath(config["Dashboard:AppPath"] ?? "/") // Deployed at root site level '/'
            .WithAppPool(config["Dashboard:AppPoolName"] ?? "AppPipeDashboardPool")
            .WithServiceDisplayName("AppPipe Telemetry Dashboard")
            .WithServiceDescription("Orchestrates AppPipe microservices and renders telemetry.");

        // 4. Register and configure BackendWorker
        var backendPassword = config["BackendWorker:ServicePassword"];
        var backend = builder.AddProject("BackendWorker")
            .WithEndpoint(7002)
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithAppPool(config["BackendWorker:AppPoolName"] ?? "CustomBackendPool")
            .WithIISSite(config["BackendWorker:IISSiteName"] ?? "Default Web Site")
            .WithAppPath("/backend") // Deployed under /backend instead of /BackendWorker
            .WithServiceDisplayName("AppPipe Backend Worker Service")
            .WithServiceDescription("Processes long-running background tasks and OTLP logs.")
            .WithServiceStartType("auto")
            .WithServiceAccount(config["BackendWorker:ServiceAccount"] ?? "LocalSystem")
            .WithServicePassword(backendPassword);

        // 5. Register and configure FrontendApi (declaring dependency on BackendWorker)
        var frontend = builder.AddProject("FrontendApi")
            .WithReference(backend) // Auto-injects connection variables
            .WithEndpoint(7003)
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithAppPool(config["FrontendApi:AppPoolName"] ?? "CustomFrontendPool")
            .WithIISSite(config["FrontendApi:IISSiteName"] ?? "Default Web Site")
            .WithServiceDisplayName("AppPipe Frontend API Service")
            .WithServiceDescription("Public-facing gateway and endpoint handler.")
            .WithServiceStartType("auto");

        // 6. Build the application graph
        var app = builder.Build();

        // 7. Parse execution targets
        if (args.Length > 0 && args[0].StartsWith("--deploy"))
        {
            var targetStr = args.Length > 1 ? args[1] : "iis";
            var deployPath = args.Length > 2 ? args[2] : "";

            var target = targetStr.ToLower() switch
            {
                "windows-service" or "service" => DeploymentTarget.WindowsService,
                "iis" => DeploymentTarget.IIS,
                "linux-service" or "systemd" => DeploymentTarget.LinuxService,
                "linux-nginx" => DeploymentTarget.LinuxNginx,
                "linux-caddy" => DeploymentTarget.LinuxCaddy,
                _ => throw new ArgumentException($"Unknown deployment target: {targetStr}")
            };

            await OnPremDeployer.CompileToOnPremAsync(app, target, deployPath);
        }
        else if (Environment.GetEnvironmentVariable("APP_POOL_ID") != null || 
                 Environment.GetEnvironmentVariable("WINDOWS_SERVICE") == "true")
        {
            // Running inside IIS/Service environment. Run Dashboard gateway only.
            var gateway = new GatewayHost();
            await gateway.StartAsync(string.Empty, app, app.ConfigureGatewayAction);
            await Task.Delay(-1); // Keep alive
        }
        else
        {
            // Running locally for development
            var runner = new DevHostRunner(app);
            await runner.RunAsync();
        }
    }
}
```

---

## 📄 Reference Configuration Layout (`appsettings.json`)

You can define all environment-specific parameters inside your deployment `appsettings.json`:

```json
{
  "Dashboard": {
    "Name": "ProductionDashboard",
    "IISSiteName": "Default Web Site",
    "AppPoolName": "ProductionDashboardPool",
    "UseWebSockets": false
  },
  "BackendWorker": {
    "IISSiteName": "Default Web Site",
    "AppPoolName": "ProdBackendPool",
    "ServiceAccount": "DOMAIN\\SvcBackend",
    "ServicePassword": "MySecretPasswordReference"
  },
  "FrontendApi": {
    "IISSiteName": "Default Web Site",
    "AppPoolName": "ProdFrontendPool"
  }
}
```

---

## 🏢 On-Premises Deployment Targets (IIS & Service Internals)

### 1. IIS Deployments
* **Virtual Directories**: Registers the orchestrator (Dashboard) and microservices as sub-applications under the specified IIS Site.
* **AppPool Setup**: Creates custom AppPools and binds them to the configured identities.
* **Identity Customization**: 
  * If a built-in account (e.g., `NetworkService`, `ApplicationPoolIdentity`) is set via `WithServiceAccount`, AppPool processes are configured natively to use that type.
  * If a custom account is set, the AppPool switches to `SpecificUser` and applies the user's domain username and password.
* **Self-Healing File Locking**: The build pipeline automatically runs `iisreset /stop` and stops services. It polls for 10 seconds to release file locks on the output DLLs, falling back to process termination (`Process.Kill()`) if the locks persist, ensuring future updates compile without error.
* **IIS Token Overwrite Filter**: Intercepts OTLP telemetry calls and overrides mismatches of the `MS-ASPNETCORE-TOKEN` header across different AppPools, allowing telemetry loopbacks to bypass IIS security filters.

### 2. Windows Service Deployments
* Uses native `sc.exe` executions with flat argument tokens to cleanly register the service executables.
* Configures the service name, display name, description, startup type (`auto`/`demand`), and the service execution context (RunAs credentials and passwords).

### 3. Linux systemd & Reverse Proxy Configs
* Generates systemd service unit files (`.service`) dynamically.
* If `ServiceAccount` is configured, it injects `User=<account>` in the Service section to control process execution safety.
* Automatically creates target deployment scripts/configs for **Nginx** and **Caddy** reverse proxies.

---

## 🚀 DevOps CI/CD Pipelines (Deploying Pre-Compiled DLLs)

In professional DevOps environments, you separate compilation from deployment. 

### 1. Bypassing Compilation on the Target Server
By default, AppPipe looks for `.csproj` files to run compilation on-the-fly. On your production target server, this fails as you only have compiled files.
* **The Solution**: Use the `--prepublished-dir <path>` parameter:
  ```bash
  dotnet AppPipe.DevHost.dll --deploy iis --prepublished-dir C:\inetpub\apps\AppPipe
  ```
* **Effect**: Bypasses the `.csproj` check and skips the `PublishProjectsModule` entirely, directly configuring the pre-compiled directories in IIS or Windows Services.

### 2. DevOps Pipeline Examples

#### A. GitHub Actions (YAML)
This workflow builds the projects on the runner and executes the deployment script on a Windows Self-Hosted runner:

```yaml
name: AppPipe On-Prem IIS Deployment

on:
  push:
    branches: [ main ]

jobs:
  build:
    name: Build & Package Artifacts
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '8.0.x'
        
    - name: Publish All Services
      run: |
        dotnet publish samples/AppPipe.DevHost/AppPipe.DevHost.csproj -c Release -o ./publish/AppPipe.DevHost
        dotnet publish samples/BackendWorker/BackendWorker.csproj -c Release -o ./publish/BackendWorker
        dotnet publish samples/FrontendApi/FrontendApi.csproj -c Release -o ./publish/FrontendApi
        
    - name: Upload Artifact
      uses: actions/upload-artifact@v3
      with:
        name: apppipe-packages
        path: ./publish

  deploy:
    name: Deploy to Production Server
    needs: build
    runs-on: [self-hosted, windows] # Self-hosted runner installed on the IIS web server
    steps:
    - name: Download Artifacts
      uses: actions/download-artifact@v3
      with:
        name: apppipe-packages
        path: C:\inetpub\apps\AppPipe
        
    - name: Run AppPipe IIS Deployer
      run: |
        dotnet C:\inetpub\apps\AppPipe\AppPipe.DevHost\AppPipe.DevHost.dll --deploy iis --prepublished-dir C:\inetpub\apps\AppPipe
      env:
        # Securely inject environment configuration and secrets
        APPIPE__Dashboard__UseWebSockets: "false"
        APPIPE__BackendWorker__AppPoolName: "ProductionBackendPool"
        APPIPE__BackendWorker__ServiceAccount: "DOMAIN\\SvcAccount"
        APPIPE__BackendWorker__ServicePassword: ${{ secrets.IIS_SVC_ACCOUNT_PASSWORD }}
```

#### B. Azure DevOps (YAML)
A similar configuration setup utilizing Azure Pipelines environment environments and secure variables:

```yaml
trigger:
- main

variables:
  # Secret variables like $(iis.svc.password) are configured in the Azure DevOps Variable Group
  APPIPE__BackendWorker__ServicePassword: $(iis.svc.password)
  APPIPE__BackendWorker__ServiceAccount: 'DOMAIN\SvcAccount'
  APPIPE__BackendWorker__AppPoolName: 'ProductionBackendPool'

stages:
- stage: BuildStage
  jobs:
  - job: BuildJob
    pool:
      vmImage: 'windows-latest'
    steps:
    - task: DotNetCoreCLI@2
      displayName: 'Publish Orchestrator'
      inputs:
        command: 'publish'
        publishWebProjects: false
        projects: 'samples/AppPipe.DevHost/AppPipe.DevHost.csproj'
        arguments: '-c Release -o $(Build.ArtifactStagingDirectory)/AppPipe.DevHost'
        
    - task: DotNetCoreCLI@2
      displayName: 'Publish BackendWorker'
      inputs:
        command: 'publish'
        publishWebProjects: false
        projects: 'samples/BackendWorker/BackendWorker.csproj'
        arguments: '-c Release -o $(Build.ArtifactStagingDirectory)/BackendWorker'

    - task: DotNetCoreCLI@2
      displayName: 'Publish FrontendApi'
      inputs:
        command: 'publish'
        publishWebProjects: false
        projects: 'samples/FrontendApi/FrontendApi.csproj'
        arguments: '-c Release -o $(Build.ArtifactStagingDirectory)/FrontendApi'

    - task: PublishBuildArtifacts@1
      inputs:
        PathtoPublish: '$(Build.ArtifactStagingDirectory)'
        ArtifactName: 'drop'

- stage: DeployStage
  dependsOn: BuildStage
  jobs:
  - deployment: DeployIIS
    pool:
      name: 'OnPremServersPool' # Deployment group target pool on-premises
    environment: 'Production'
    strategy:
      runOnce:
        deploy:
          steps:
          - task: DownloadBuildArtifacts@1
            inputs:
              buildType: 'current'
              downloadType: 'single'
              artifactName: 'drop'
              downloadPath: 'C:\inetpub\apps\AppPipe'
              
          - task: PowerShell@2
            displayName: 'Execute Deployer'
            inputs:
              targetType: 'inline'
              script: |
                dotnet C:\inetpub\apps\AppPipe\drop\AppPipe.DevHost\AppPipe.DevHost.dll --deploy iis --prepublished-dir C:\inetpub\apps\AppPipe\drop
            env:
              # Secret bindings are automatically mapped via variables
              APPIPE__BackendWorker__ServicePassword: $(APPIPE__BackendWorker__ServicePassword)
```

---

## 🛠️ CLI Troubleshooting & Verification Commands

Here are common diagnostic commands to execute when checking on-premises status:

### 1. Querying IIS Status via Command Line
Run these from an Administrator Command Prompt to verify sites and application pools:

```bash
# List all running IIS Application Pools
C:\windows\system32\inetsrv\appcmd.exe list apppool

# List all applications and their physical paths
C:\windows\system32\inetsrv\appcmd.exe list app /text:*

# Recycle a specific AppPool
C:\windows\system32\inetsrv\appcmd.exe recycle apppool /apppool.name:ProductionBackendPool
```

### 2. Troubleshooting Port Conflicts (Error 502.5 / Socket Exceptions)
If your AppPool fails to start or crashes immediately due to a `SocketException (10048)`, verify if another service is holding the OTLP port:

```powershell
# In PowerShell: find what process ID is holding the target port (e.g. 63304)
Get-NetTCPConnection -LocalPort 63304 | Select-Object LocalPort, State, OwningProcess

# Get the process details by ID
Get-Process -Id <OwningProcessId>

# Stop Windows Services that might be conflicting
sc.exe stop AppPipeDashboard
sc.exe stop BackendWorker
sc.exe stop FrontendApi
```

### 3. Reading IIS Application stdout Logs
If the AppPool is failing, enable standard output logging by editing the `web.config` inside your published folder:

1. Open `web.config` and change `stdoutLogEnabled` to `true`:
   ```xml
   <aspNetCore processPath="dotnet" arguments=".\AppPipe.DevHost.dll" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" ... />
   ```
2. Create a folder named `logs` in the published root directory.
3. Access the application in the browser and read the crash log output saved in `.\logs\stdout_xxxxx.log`.

