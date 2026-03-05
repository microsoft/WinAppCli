# Microsoft.Windows.SDK.BuildTools.WinApp

Enables `dotnet run` for packaged Windows applications.

## Overview

This package provides MSBuild targets that seamlessly integrate with the .NET CLI, enabling developers to build, register debug identity, and launch packaged Windows applications with a simple `dotnet run` command.

## Features

- **Automatic Detection**: Detects when your project is a packaged WinUI/WinAppSDK application
- **Seamless Integration**: Hooks into the standard `dotnet run` pipeline
- **Debug Identity**: Automatically registers debug identity for development
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
4. Registers debug identity with the Windows shell
5. Launches the application using the Windows Application Activation Manager

## Requirements

- Windows 10 or later
- .NET 8.0 or later
- Windows App SDK 1.4 or later

## Troubleshooting

### Application fails to launch

Ensure your `appxmanifest.xml` is correctly configured with:
- Valid Identity (Name, Publisher, Version)
- Valid Application entry (Id, Executable, EntryPoint)

### Debug identity registration fails

Run Visual Studio or the terminal as Administrator, or ensure Developer Mode is enabled in Windows Settings.

## License

MIT License - see LICENSE file for details.
