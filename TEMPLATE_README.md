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
dotnet new apppipe-system -n MySystem
```

This generates:
* **`MySystem.sln`**: The Visual Studio solution.
* **`MySystem.AppHost`**: The AppPipe gateway reverse proxy and telemetry dashboard.
* **`MySystem.ApiService`**: A backend REST API configured with OpenTelemetry.
* **`MySystem.Web`**: A frontend web application that communicates with the API service using service discovery variables.

## 📄 License
This project is licensed under the MIT License.
