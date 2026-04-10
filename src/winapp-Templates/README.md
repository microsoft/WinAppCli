# Windows App SDK Project Templates

This package provides `dotnet new` templates for Windows App SDK (WinUI) applications.

## Installation

```bash
dotnet new install Microsoft.WindowsAppSDK.Templates
```

## Available Templates

| Template | Short Name | Description |
|----------|------------|-------------|
| WinUI App | `winui` | A WinUI 3 desktop application with `dotnet run` support |
| WinUI Class Library | `winuilib` | A class library for WinUI applications |

## Quick Start

Create a new WinUI application:

```bash
dotnet new winui -n MyApp
cd MyApp
dotnet run
```

## Features

- **dotnet run support**: Run packaged WinUI apps directly from the command line
- **Pre-configured manifest**: AppxManifest.xml ready for packaging
- **Development certificates**: Easy setup for local development
- **Visual Studio compatible**: Project structure matches Visual Studio templates

## Requirements

- Windows 10 or later
- .NET 8 SDK or later
- Windows App SDK 1.4 or later (automatically referenced)

## Uninstalling

```bash
dotnet new uninstall Microsoft.WindowsAppSDK.Templates
```

## License

MIT License
