# Microsoft.Windows.SDK.BuildTools.WinApp

Enables `dotnet run` for packaged Windows applications.

## Overview

This package provides MSBuild targets that seamlessly integrate with the .NET CLI, enabling developers to build and launch packaged Windows applications with a simple `dotnet run` command. Under the hood, it invokes `winapp run` to create a loose layout package, register it with Windows, and launch the app — simulating a full MSIX install for debugging.

## Features

- **Automatic Detection**: Detects when your project is a packaged WinUI/WinAppSDK application
- **Seamless Integration**: Hooks into the standard `dotnet run` pipeline, invoking `winapp run` automatically
- **Loose Layout Package**: Registers your build output as a loose layout package with Windows (like a real MSIX install)
- **Zero Configuration**: Works out of the box with standard WinUI project templates

## Usage

1. Add this package to your WinUI project:

```xml
<PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinApp" Version="0.1.10" PrivateAssets="all" />
```

2. Run your application:

```bash
dotnet run
```

## How It Works

When you run `dotnet run`, this package:

1. Builds your project normally
2. Detects if the project uses Windows App SDK with packaging
3. Prepares a loose-layout package in the output directory
4. Registers the package with Windows via `winapp run` (like a real MSIX install)
5. Launches the application using the Windows Application Activation Manager

## Requirements

- Windows 10 or later
- .NET 8.0 or later
- Windows App SDK 1.4 or later

## Configuration

Set these MSBuild properties in your `.csproj` to customize behavior:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppRunUseExecutionAlias` | `false` | Launch via execution alias instead of AUMID activation. Useful for console apps that need terminal I/O. |
| `WinAppRunNoLaunch` | `false` | Only register identity without launching the app |
| `WinAppRunDebugOutput` | `false` | Capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time (prevents VS/VS Code). Use `WinAppRunNoLaunch` instead to attach a different debugger. Cannot be combined with `WinAppRunNoLaunch`. |

Example:

```xml
<PropertyGroup>
  <!-- Launch via execution alias so console I/O stays in the current terminal -->
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>

  <!-- Capture OutputDebugString messages and first-chance exceptions -->
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
```

## Troubleshooting

### Application fails to launch

Ensure your `appxmanifest.xml` is correctly configured with:
- Valid Identity (Name, Publisher, Version)
- Valid Application entry (Id, Executable, EntryPoint)

### Debug identity registration fails

Run Visual Studio or the terminal as Administrator, or ensure Developer Mode is enabled in Windows Settings.

## License

MIT License - see LICENSE file for details.
