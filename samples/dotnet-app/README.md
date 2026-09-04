# .NET Console Sample Application

This sample demonstrates how to use winapp CLI with a .NET console application to add package identity and package as MSIX.

For a complete step-by-step guide, see the [.NET Getting Started Guide](../../docs/guides/dotnet.md).

## What This Sample Shows

- Basic .NET console application with automatic .NET project detection by `winapp init`
- Using Windows Runtime APIs to retrieve package identity
- NuGet package references (`Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`) added directly to `.csproj` by `winapp init`
- Using `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package for automatic `dotnet run` support with package identity
- Using **execution alias** (`WinAppRunUseExecutionAlias`) so console output stays in the current terminal
- Using Windows App SDK via NuGet for modern Windows APIs
- MSIX packaging with app manifest and assets

## Prerequisites

- .NET 10.0 SDK
- winapp CLI built locally: run `.\scripts\build-cli.ps1` from the repo root (this builds the CLI and produces the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package in `artifacts/nuget/`)

## Setup

Run `winapp init` in this directory. It auto-detects the `.csproj` and runs the .NET-specific setup flow:

```powershell
winapp init
```

This will validate the `TargetFramework`, add required NuGet packages to the `.csproj`, and generate the manifest, assets, and development certificate. No `winapp.yaml` is needed for .NET projects.

## Building and Running

The `.csproj` includes the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, which hooks into `dotnet run` to automatically register a loose layout package with identity and launch the app.

Because this is a console app, the project sets `WinAppRunUseExecutionAlias` to `true` so the app runs via its execution alias. This keeps console I/O in the current terminal instead of opening a new window:

```powershell
dotnet run
```

Output: 
```
Package Family Name: dotnet-app_12345abcde
Windows App Runtime Version: 1.8-stable (1.8.0)
```

> **Note:** The execution alias (`dotnet-app.exe`) is defined in `appxmanifest.xml` via `uap5:AppExecutionAlias`. You can add one with `winapp manifest add-alias`.

### Package as MSIX

The `.csproj` is configured to automatically package when publishing:

```powershell
# Create a dev certificate (first time only)
winapp cert generate --if-exists skip

# Publish and package in one command
dotnet publish

# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx

# Install the generated MSIX
# The .msix file will be in the root directory
```

Double-click the `.msix` file to install, then run from anywhere:

```powershell
dotnet-app
```

## Native AOT runtime acceptance

The repository includes a real-device acceptance runner that publishes, launches, and verifies
an isolated packaged WinUI fixture and a long-running unpackaged Native AOT console fixture:

```powershell
# x64
.\scripts\test-native-aot-run.ps1 -Architecture x64

# Run this command on Windows ARM64
.\scripts\test-native-aot-run.ps1 -Architecture arm64 `
  -WinappPath .\artifacts\cli\win-arm64\winapp.exe
```

The ARM64 command is a runtime test and must run on an ARM64 device with the ARM64 C++ build tools.
It validates process survival, published/staged provenance, package registration, and the absence
of CoreCLR/JIT modules, then cleans up its process and development registration.