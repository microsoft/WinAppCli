# WPF Sample Application

This sample demonstrates how to use winapp CLI with a WPF application to add package identity and package as MSIX.

For a complete step-by-step guide, see the [.NET Getting Started Guide](../../docs/guides/dotnet.md) - the same steps apply to WPF, WinForms, and other .NET UI frameworks.

## What This Sample Shows

- WPF desktop application with modern UI
- Using Windows Runtime APIs to retrieve package identity
- Using **Win2D** (`Microsoft.Graphics.Win2D`) — a third-party WinRT component that requires activatable class registration
- NuGet package references (`Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, `Microsoft.Graphics.Win2D`) added directly to `.csproj`
- Using `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package for automatic `dotnet run` support with package identity
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

### Run 

The `.csproj` includes the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, which hooks into `dotnet run` to automatically register a loose layout package with identity and launch the app:

```powershell
dotnet run
```

The WPF window will display:
```
Package Family Name: wpf-app.debug_12345abcde
Windows App Runtime Version: 1.8-stable (1.8.0)
Win2D: <GPU device description>
```

The Win2D line confirms that `CanvasDevice` was activated successfully, which requires the InProcessServer entries for `Microsoft.Graphics.Canvas.dll` to be registered.

> **Note:** Win2D requires a platform-specific build (not AnyCPU). Use `-r win-x64` or `-r win-arm64` when building or packaging.

### Package as MSIX

The `.csproj` is configured to automatically run `winapp pack` after `dotnet publish` using the winapp CLI bundled in the NuGet package. If a `devcert.pfx` certificate exists in the project directory, the MSIX will be signed automatically; otherwise, an unsigned MSIX is produced.

```powershell
# Publish and package in one command (produces an unsigned MSIX)
dotnet publish

# To produce a signed MSIX, first generate a dev certificate:
winapp cert generate --if-exists skip

# Then publish again — the certificate will be picked up automatically
dotnet publish

# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx

# Install the generated MSIX
# The .msix file will be in the project directory
```

Double-click the `.msix` file to install. The app will be available in your Start Menu and can be launched like any other installed app.