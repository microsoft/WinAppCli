# dotnet run Support for Packaged WinUI Apps

This document describes the implementation of `dotnet run` support for packaged WinUI applications using a custom NuGet package.

## Overview

The solution enables developers to run packaged WinUI (WinAppSDK) applications using just the .NET CLI:

```bash
winapp init
dotnet run
```

## Architecture

### Components

1. **Microsoft.Windows.SDK.BuildTools.WinApp** (NuGet Package)
   - Contains the WinAppCLI binary in `tools/` folder
   - Provides MSBuild targets that hook into `dotnet run`
   - Automatically detects packaged WinUI apps and handles launch

2. **WinAppCLI**
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
Run Target (invokes: winapp run --manifest ...)
    │
    ▼
WinAppCLI
    ├── Creates loose-layout package
    ├── Registers debug identity
    └── Launches via Application Activation Manager
```

## File Structure

```
src/
├── winapp-NuGet/                           # BuildTools.WinApp NuGet package
│   ├── Microsoft.Windows.SDK.BuildTools.WinApp.csproj
│   ├── README.md
│   ├── build/
│   │   ├── Microsoft.Windows.SDK.BuildTools.WinApp.props
│   │   └── Microsoft.Windows.SDK.BuildTools.WinApp.targets
│   └── tools/                              # CLI binaries (copied by build script)
│       ├── win-x64/
│       └── win-arm64/
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
| `WinAppCliPath` | (in package) | Path to the winapp.exe CLI |
| `WinAppRunUseExecutionAlias` | `false` | Launch via execution alias instead of AUMID. Keeps console I/O in the current terminal. Requires `uap5:ExecutionAlias` in the manifest. Cannot be combined with `WinAppRunNoLaunch`. |
| `WinAppRunNoLaunch` | `false` | Only register package identity without launching the app. Cannot be combined with `WinAppRunUseExecutionAlias`. |
| `WinAppRunDebugOutput` | `false` | Attach as a debugger to capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time, so Visual Studio or VS Code cannot debug simultaneously. Use `WinAppRunNoLaunch` instead to attach a different debugger. Cannot be combined with `WinAppRunNoLaunch`. |

### Targets (Microsoft.Windows.SDK.BuildTools.WinApp.targets)

| Target | Description |
|--------|-------------|
| `_WinAppValidateRunSupport` | Validates prerequisites (CLI exists, manifest exists) |
| `_WinAppBuildRunArgs` | Builds CLI command arguments (shared by run targets) |
| `_WinAppPrepareRunArguments` | Overrides RunCommand to use CLI |
| `RunPackagedApp` | Direct target to run packaged app |
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
```

## Usage

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

Launch via execution alias (for console apps):
```xml
<PropertyGroup>
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
</PropertyGroup>
```

Register identity without launching:
```xml
<PropertyGroup>
  <WinAppRunNoLaunch>true</WinAppRunNoLaunch>
</PropertyGroup>
```

Capture OutputDebugString messages and first-chance exceptions:
```xml
<PropertyGroup>
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
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

### 2. Developer Mode Requirement

Running packaged apps requires Developer Mode enabled on Windows. The solution should:
- Detect when Developer Mode is disabled
- Provide clear error messages
- Consider documenting this requirement prominently

### 3. First-run Experience

On first `dotnet run`, the CLI needs to:
- Download Windows SDK Build Tools (if not cached)
- This can take time on slow connections

Consider pre-caching or documenting this.

### 4. Platform Detection

The current implementation defaults to x64. For ARM64 machines, the targets correctly detect architecture, but the default Platform may need adjustment.

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

1. **Hot Reload Support**: Integrate with `dotnet watch` for live reloading
2. **Debug Attachment**: Return process ID for debugger attachment in IDEs
3. **Unpackaged Mode**: Auto-detect and use unpackaged mode when appropriate
4. **Multiple Apps**: Support projects with multiple Application entries in manifest
