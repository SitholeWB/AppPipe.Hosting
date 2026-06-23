# AppPipe.Hosting.Templates 🚀

Scaffolding templates for **AppPipe.Hosting**—a lightweight, on-premises alternative to the **.NET Aspire** dashboard and gateway runner.

This template pack allows you to scaffold a complete multi-project starter system configured for dynamic service discovery and OpenTelemetry ingestion out of the box.

## 📦 Installation

To install the template pack from NuGet:
```bash
dotnet new install AppPipe.Hosting.Templates
```

## 🚀 Usage

Scaffold a new AppPipe solution in a clean directory:
```bash
dotnet new app-pipe -n MySystem
```

This generates:
* **`MySystem.sln`**: The Visual Studio solution.
* **`MySystem.AppHost`**: The AppPipe gateway reverse proxy and telemetry dashboard.
* **`MySystem.ApiService`**: A backend REST API configured with OpenTelemetry.
* **`MySystem.Web`**: A frontend web application that communicates with the API service using service discovery variables.

## ⚙️ Template Configuration Choices

When scaffolding with `dotnet new app-pipe`, you can customize your architecture, frontend, database, auth, and caching options:

| Parameter | Choice Option | Default | Description |
| :--- | :--- | :--- | :--- |
| **`-ar, --architecture`** | `simple`, `clean-cqrs` | `simple` | Choose `simple` for a Minimal API structure, or `clean-cqrs` for a Clean Architecture layered solution. |
| **`-da, --database`** | `none`, `sqlite`, `postgresql`, `sqlserver` | `none` | Configures Entity Framework Core DB context persistence. |
| **`-f, --frontend`** | `blazor`, `htmx` | `blazor` | Scaffolds either a Blazor Server SSR UI or Razor Pages + HTMX UI, styled with a premium Outfit theme. |
| **`-au, --auth`** | `none`, `jwt` | `none` | Configures JWT Bearer authentication validation middleware and token generation endpoints. |
| **`-c, --caching`** | `none`, `redis` | `none` | Configures Redis distributed caching in command/query handlers. |

For example, to scaffold a full production CQRS architecture with a Blazor frontend, SQLite database, secure JWT authorization, and Redis caching:
```bash
dotnet new app-pipe -n MySystem --architecture clean-cqrs --database sqlite --auth jwt --caching redis
```

## 📄 License
This project is licensed under the MIT License.
