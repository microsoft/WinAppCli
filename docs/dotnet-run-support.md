# dotnet run Support for Packaged WinUI Apps

This document describes the implementation of `dotnet run` support for packaged WinUI applications using a custom NuGet package.

## Overview

The solution enables developers to run packaged WinUI (WinAppSDK) applications using just the .NET CLI:

```bash
dotnet new winui
dotnet run
```

## Architecture

### Components

1. **Microsoft.Windows.SDK.BuildTools.WinApp** (NuGet Package)
   - Contains the WinAppCLI binary in `tools/` folder
   - Provides MSBuild targets that hook into `dotnet run`
   - Automatically detects packaged WinUI apps and handles launch

2. **Microsoft.WindowsAppSDK.Templates** (NuGet Package)
   - Provides `dotnet new winui` template
   - Pre-configured with BuildTools.Extras package reference
   - Mirrors Visual Studio WinUI template structure

3. **WinAppCLI**
   - Handles debug identity registration
   - Launches packaged apps via Windows Application Activation Manager

### How It Works

```
dotnet run
    │
    ▼
MSBuild Build Target
    │
    ▼
_WinAppValidateRunSupport (validates prerequisites, WindowsPackageType != None)
    │
    ▼
_WinAppPrepareRunArguments (overrides RunCommand with CLI path)
    │
    ▼
Run Target (invokes: winapp run <output-path> --caller nuget-package ...)
    │
    ▼
WinAppCLI
    ├── Locates/auto-detects AppxManifest.xml
    ├── Injects AppExecutionAlias into manifest
    ├── Creates and registers loose-layout package
    ├── Enables package debugging (PLM disabled)
    ├── Launches app via execution alias (Process.Start)
    ├── Captures stdout/stderr via redirected streams
    ├── Captures Debug.WriteLine/OutputDebugString via Win32 Debug API
    ├── Streams all output to terminal until app exits
    └── Cleans up (DisableDebugging, process cleanup)
```

#### Output Capture Architecture

The default alias-based launch provides two independent capture mechanisms:

- **Stdio capture** (always available): The execution alias process is launched via `Process.Start` with redirected stdout/stderr streams. Environment variables (including `DOTNET_*` for hot-reload) are inherited automatically.
- **Debug API capture** (optional): `DebugActiveProcess` attaches to capture `OutputDebugString` calls and first-chance exceptions. This is mutually exclusive with managed debuggers (VS/VS Code).

When `--output-filter stdout,stderr` is specified (or `WinAppOutputFilter` MSBuild property), the Debug API is not attached, leaving the process free for managed debugger attachment while still capturing stdio.

## File Structure

```
src/
├── winapp-NuGet/                           # BuildTools.Extras NuGet package
│   ├── Microsoft.Windows.SDK.BuildTools.WinApp.csproj
│   ├── README.md
│   ├── build/
│   │   ├── Microsoft.Windows.SDK.BuildTools.WinApp.props
│   │   └── Microsoft.Windows.SDK.BuildTools.WinApp.targets
│   └── tools/                              # CLI binaries (copied by build script)
│       ├── win-x64/
│       └── win-arm64/
│
├── winapp-Templates/                       # Templates NuGet package
│   ├── Microsoft.WindowsAppSDK.Templates.csproj
│   ├── README.md
│   └── templates/
│       └── winui/                          # WinUI app template
│           ├── .template.config/
│           │   └── template.json
│           ├── WinUIApp1.csproj
│           ├── App.xaml
│           ├── App.xaml.cs
│           ├── MainWindow.xaml
│           ├── MainWindow.xaml.cs
│           ├── app.manifest
│           ├── Package.appxmanifest
│           ├── Properties/
│           └── Assets/
│
samples/
└── winui-app/                              # Sample WinUI app for testing
```

## MSBuild Integration Details

### Properties (Microsoft.Windows.SDK.BuildTools.WinApp.props)

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppManifestPath` | Auto-detected | Path to the AppxManifest file |
| `WinAppLooseLayoutPath` | `$(OutputPath)AppX\` | Output directory for loose-layout package |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppOutputFilter` | (empty = all) | Output categories to capture: `stdout`, `stderr`, `debug`, `debug-all`, `exception`. Set to `stdout,stderr` to allow VS/VS Code debugger attachment |
| `WinAppCliPath` | (in package) | Path to the winapp.exe CLI |

### Targets (Microsoft.Windows.SDK.BuildTools.WinApp.targets)

| Target | Description |
|--------|-------------|
| `_WinAppValidateRunSupport` | Validates prerequisites (CLI exists, manifest exists) |
| `_WinAppBuildRunArgs` | Builds CLI command arguments (shared by run targets) |
| `_WinAppPrepareRunArguments` | Overrides RunCommand/RunArguments to use CLI via `ComputeRunArguments` |
| `_WinAppDeduplicateDesignTimeCompile` | Deduplicates XAML-generated .g.cs files for C# DevKit IntelliSense |
| `_WinAppCopyContentToLooseLayout` | Copies Content/None items to the loose-layout AppX directory |
| `RunPackagedApp` | Direct target to run packaged app (alternative to `dotnet run`) |
| `WinAppRunSupportInfo` | Diagnostic target showing all properties |

### Detection Logic

The package detects a packaged app when:
1. `WindowsPackageType` is **not** set to `None` (absence of the property means packaged)

## Build Scripts

### package-nuget.ps1

Creates both NuGet packages:

```powershell
.\scripts\package-nuget.ps1                    # Prerelease version
.\scripts\package-nuget.ps1 -Version 1.0.0 -Stable  # Stable version
```

### Integration with build-cli.ps1

The main build script now includes NuGet packaging:

```powershell
.\scripts\build-cli.ps1                        # Full build including NuGet
.\scripts\build-cli.ps1 -SkipNuGet             # Skip NuGet packages
.\scripts\build-cli.ps1 -SkipVsc              # Skip VS Code extension
```

## Usage

### Installing the Template

```bash
# From local build
dotnet nuget add source "path/to/artifacts/nuget" --name WinAppLocal
dotnet new install Microsoft.WindowsAppSDK.Templates::1.0.0-test --nuget-source WinAppLocal

# From NuGet.org (when published)
dotnet new install Microsoft.WindowsAppSDK.Templates
```

### Creating and Running a WinUI App

```bash
dotnet new winui -n MyApp
cd MyApp
dotnet run
```

### Customization

Disable run support for a project:
```xml
<PropertyGroup>
  <EnableWinAppRunSupport>false</EnableWinAppRunSupport>
</PropertyGroup>
```

Specify manifest path:
```xml
<PropertyGroup>
  <WinAppManifestPath>$(MSBuildProjectDirectory)\custom\appxmanifest.xml</WinAppManifestPath>
</PropertyGroup>
```

Pass launch arguments:
```xml
<PropertyGroup>
  <WinAppLaunchArgs>--debug --verbose</WinAppLaunchArgs>
</PropertyGroup>
```

Allow VS/VS Code debugger attachment (disable debug API capture):
```xml
<PropertyGroup>
  <WinAppOutputFilter>stdout,stderr</WinAppOutputFilter>
</PropertyGroup>
```

Or from the command line for a single run:
```bash
dotnet run -p:WinAppOutputFilter=stdout,stderr
```

## Outstanding Production Blockers

### 1. CLI AOT Build Issues (BLOCKING)

The CLI currently has NativeAOT compilation errors related to Newtonsoft.Json and NuGet.Protocol. These must be resolved before the NuGet package can include the CLI binaries.

**Error summary:**
- 146 trim/AOT analysis errors
- Related to reflection-heavy code in Newtonsoft.Json
- Related to dynamic code generation in NuGet.Protocol

**Resolution:**
- Wait until https://github.com/NuGet/Home/issues/14408

### 2. Template Certificate Generation

The template creates projects without development certificates. Users will need to:
- Run `winapp init` to generate certificates, OR
- Use unpackaged mode first, OR
- Add certificate generation to the template post-action

### 3. Developer Mode Requirement

Running packaged apps requires Developer Mode enabled on Windows. The solution should:
- Detect when Developer Mode is disabled
- Provide clear error messages
- Consider documenting this requirement prominently

### 4. First-run Experience

On first `dotnet run`, the CLI needs to:
- Download Windows SDK Build Tools (if not cached)
- This can take time on slow connections

Consider pre-caching or documenting this.

### 5. Platform Detection

The current implementation defaults to x64. For ARM64 machines, the targets correctly detect architecture, but the template's default Platform may need adjustment.

## Testing

### Local Testing (without published NuGet)

The sample project imports the MSBuild targets directly:

```xml
<Import Project="..\..\src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.props" />
<Import Project="..\..\src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.targets" />
```

### Diagnostic Commands

```bash
# Show MSBuild property values
dotnet msbuild -t:WinAppRunSupportInfo

# Verbose build output
dotnet run -v:detailed
```

## Future Enhancements

1. **Unpackaged Mode**: Auto-detect and use unpackaged mode when appropriate
2. **Certificate Management**: Template could include cert generation post-action
3. **Multiple Apps**: Support projects with multiple Application entries in manifest
