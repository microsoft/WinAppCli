# .NET Console Sample Application

This sample demonstrates how to use winapp CLI with a .NET console application to add package identity and package as MSIX.

For a complete step-by-step guide, see the [.NET Getting Started Guide](../../docs/guides/dotnet.md).

## What This Sample Shows

- Basic .NET console application with automatic .NET project detection by `winapp init`
- Using Windows Runtime APIs to retrieve package identity
- NuGet package references (`Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`) added directly to `.csproj` by `winapp init`
- Configuring MSBuild to automatically apply debug identity after building in Debug configuration
- Using Windows App SDK via NuGet for modern Windows APIs
- MSIX packaging with app manifest and assets

## Prerequisites

- .NET 10.0 SDK
- winapp CLI installed via winget: `winget install Microsoft.winappcli --source winget`

## Setup

Run `winapp init` in this directory. It auto-detects the `.csproj` and runs the .NET-specific setup flow:

```powershell
winapp init
```

This will validate the `TargetFramework`, add required NuGet packages to the `.csproj`, and generate the manifest, assets, and development certificate. No `winapp.yaml` is needed for .NET projects.

## Building and Running

The `.csproj` is configured to automatically apply debug identity when building in Debug configuration:

```powershell
dotnet run
```

Output: 
```
Package Family Name: dotnet-app_12345abcde
Windows App Runtime Version: 1.8-stable (1.8.0)
```

### Package as MSIX

The `.csproj` is also configured to automatically package when building in Release mode:

```powershell
# Create a dev certificate (first time only)
winapp cert generate --if-exists skip

# Build and package in one command
dotnet build -c Release

# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx

# Install the generated MSIX
# The .msix file will be in the root directory
```

Double-click the `.msix` file to install, then run from anywhere:

```powershell
dotnet-app
```