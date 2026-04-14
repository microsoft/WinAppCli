# Getting Started with WinApp CLI

Build, run, and debug packaged Windows applications from any editor or terminal — no Visual Studio required.

## Prerequisites

- **VS Code** (recommended) — install via `winget install Microsoft.VisualStudioCode` or from [code.visualstudio.com](https://code.visualstudio.com)

For the .NET scenarios below, you also need:

- **.NET 10 SDK** (or later) — install via `winget install Microsoft.DotNet.SDK.10` or from [dot.net](https://dot.net)
- **C# Dev Kit extension** for VS Code — install from the Extensions sidebar (required for .NET IntelliSense and build support)

---

## Installation

Open PowerShell in the folder containing the setup files and run:

```powershell
.\setup-winapprun.ps1
```

The script will prompt for administrator elevation (required to trust the MSIX signing certificate), then install everything you need to build packaged Windows apps across .NET, Electron, Flutter, C++/CMake, Rust, Tauri, and more:

- **WinApp CLI** (`winapp`) — installed as an MSIX package, available system-wide. Initialize projects, run apps as packaged, generate manifests, manage certificates, and create MSIX packages.
- **WinApp VS Code Extension** — Command Palette integration and a `winapp` debug configuration for F5 launch-and-attach (.NET, C++, Node.js).
- **dotnet templates** — `dotnet new winui` for WinUI 3 apps pre-configured for packaged development. (This is a temporary template — Niels and Gordon's team are working on updated dotnet templates to replace it.)
- **MSIX Extras NuGet package** — registered as a local NuGet feed. MSBuild targets that make `dotnet run` launch with package identity automatically.

After the script finishes, verify the installation:

```powershell
winapp --version         # should print the CLI version
dotnet new list winui    # should list the WinUI template
```

> **Note:** This guide walks through .NET scenarios first. Support for Electron, Flutter, C++, and other stacks is actively being built using the same underlying CLI.

---

## Scenario 1: WinUI App from Template (.NET)

This is the fastest path for .NET developers. The `dotnet new winui` template comes pre-configured with the MSIX Extras package, an `appxmanifest.xml`, and the correct target framework. No additional initialization is needed.

### Create and run

```powershell
mkdir MyWinUIApp
cd MyWinUIApp

dotnet new winui
dotnet run
```

That's it. `dotnet run` builds the project, then the MSIX Extras package automatically calls `winapp run` under the hood. Your app launches with full package identity — you can call any Windows SDK or Windows App SDK API that requires it.

### Debug with F5 in VS Code

1. Open the project folder in VS Code:
   ```powershell
   code .
   ```

2. Open the **Run and Debug** panel (Ctrl+Shift+D) and click **create a launch.json file**.

3. Select **WinApp** from the debugger list. This generates a `launch.json` with the following configuration:

   ```jsonc
   {
     "version": "0.2.0",
     "configurations": [
       {
         "type": "winapp",
         "request": "launch",
         "name": "WinApp: Launch and Attach"
       }
     ]
   }
   ```

   Optionally, set `inputFolder` to point directly at your build output to skip the folder picker:

   ```jsonc
   {
     "version": "0.2.0",
     "configurations": [
       {
         "type": "winapp",
         "request": "launch",
         "name": "WinApp: Launch and Attach",
         "inputFolder": "${workspaceFolder}\\bin\\Debug\\net10.0-windows10.0.26100.0"
       }
     ]
   }
   ```

4. Press **F5**. If `inputFolder` is not set, the extension scans the workspace for folders containing `.exe` files and lets you pick the build output. It then registers a loose-layout package, launches the app, and attaches the debugger.

You get the full debugging experience: breakpoints, call stack, locals, watch — all running as a packaged app. The `debuggerType` launch option controls which debugger is used (default is `coreclr` for .NET; set it to `cppvsdbg` for native C++ or `node` for Node.js/Electron apps).

> **Multiple build configurations:** If you have build output for more than one configuration (e.g., `Debug` and `Release`, or `win-arm64` and `win-x64`), the extension shows a picker so you can choose which one to launch.

---

## Scenario 2: WPF App with WinApp Init (.NET)

The `winapp init` command detects your technology stack and configures the project for packaged development automatically.

### Create, initialize, and run

```powershell
mkdir MyWpfApp
cd MyWpfApp

dotnet new wpf
winapp init
dotnet run
```

`winapp init` detects the `.csproj`, updates the target framework, adds the required NuGet packages (Windows App SDK, SDK Build Tools, MSIX Extras), generates an `appxmanifest.xml` with icon assets, and installs the Windows App SDK runtime. After that, `dotnet run` launches your WPF app with full package identity — just like Scenario 1.

The `init` command is interactive by default. To accept all defaults:

```powershell
winapp init --use-defaults
```

### Debug with F5

The same VS Code debugging setup from Scenario 1 applies. Open the folder in VS Code, create a `launch.json` with the **WinApp** configuration, and press F5.

---

## VS Code Extension Features

The WinApp extension does more than debugging. Every CLI command is accessible from the Command Palette (Ctrl+Shift+P → type "WinApp"):

| Command | What it does |
|---------|-------------|
| **WinApp: Initialize Project** | Run `winapp init` with SDK channel selection |
| **WinApp: Run Application** | Run `winapp run` in the integrated terminal |
| **WinApp: Create MSIX Package** | Package your app into an MSIX with optional signing |
| **WinApp: Generate Manifest** | Create an `appxmanifest.xml` from a template |
| **WinApp: Update Manifest Assets** | Regenerate all icon assets from a source image |
| **WinApp: Generate Certificate** | Create a development signing certificate |
| **WinApp: Install Certificate** | Trust a certificate on the local machine |
| **WinApp: Sign Package** | Code-sign an MSIX or executable |
| **WinApp: Restore Packages** | Restore SDKs from `winapp.yaml` |
| **WinApp: Update Packages** | Update SDK packages to latest versions |
| **WinApp: Run SDK Tool** | Access Windows SDK tools (makeappx, signtool, etc.) |

The extension bundles its own copy of the CLI, so these commands work even if you haven't installed the MSIX package globally.

---

## How It Works

Understanding the architecture helps when troubleshooting or customizing your setup. The core of the WinApp CLI is technology-agnostic — it operates on a folder with an `AppxManifest.xml` and an executable. The .NET-specific integrations (MSBuild targets, NuGet packages) are layers on top of this foundation.

### The `winapp run` command

`winapp run` is the core primitive that the other tools build on. Given a build output folder containing your app files (and, optionally, a manifest specified via `--manifest`), it:

1. Creates a **loose-layout package** — a folder structure with your app binaries and manifest, registered with Windows via the same APIs that Visual Studio uses.
2. Registers the package identity with the system using the Windows app deployment APIs.
3. Launches the app through the Windows Application Activation Manager.
4. Prints the **process ID** to stdout, which callers (the VS Code extension, MSBuild targets, Electron Forge, etc.) use to attach a debugger.

This works regardless of how the app was built — whether from `dotnet build`, `cmake`, `npm run make`, or any other build system.

### The MSIX Extras NuGet package (.NET-specific)

The `Microsoft.Windows.SDK.BuildTools.WinApp` package contains MSBuild `.props` and `.targets` files that hook into the `dotnet run` lifecycle. When you execute `dotnet run`, MSBuild's `Run` target is intercepted and redirected through `winapp run` with the correct output directory and manifest path. The package also bundles a copy of the WinApp CLI, so no global installation is required for `dotnet run` to work.

### The VS Code extension debug flow

When you press F5 with a `winapp` launch configuration:

1. If `inputFolder` is set in `launch.json`, the extension uses that directory. Otherwise, it scans the workspace for folders containing `.exe` files (ignoring `node_modules`, `obj`, `.winapp`, etc.) and presents a quick pick.
2. It invokes `winapp run <inputFolder> [--manifest <path>]` and captures the process ID from the output. If `manifest` is not set, the CLI auto-detects from the input folder or current directory.
3. It starts a child debug session using the specified `debuggerType` (default: `coreclr`) and attaches to the process.
4. When you stop debugging, the session is cleaned up automatically.

This flow is not tied to .NET — by changing `debuggerType` to `cppvsdbg` or `node`, the same F5 experience works for C++ or Electron apps.

---

## Common Tasks

```powershell
winapp manifest generate                             # generate an appxmanifest.xml
winapp manifest update-assets mylogo.png             # regenerate icons from a source image
winapp pack ./dist --cert ./devcert.pfx              # create and sign an MSIX package
```

---

## Troubleshooting

**"Developer Mode is not enabled"**
`winapp init` and `winapp run` will detect this and prompt to enable it automatically (requires elevation). You can also enable it manually under Windows Settings > For Developers.

**`dotnet run` does not launch as packaged**
Ensure the `Microsoft.Windows.SDK.BuildTools.WinApp` package reference is present in your `.csproj`. If you initialized with `winapp init`, it should already be there. Run `dotnet restore` to make sure NuGet packages are resolved (the local NuGet feed registered by the setup script must be accessible).

**F5 says "No AppxManifest.xml found"**
Build the project first (`dotnet build`), then try again. The extension looks for the manifest in your build output, not the project root.

**Certificate trust errors when installing MSIX**
Re-run the setup script as administrator to ensure the development certificate is in the TrustedPeople store.

---

## What's Next

The scenarios in this guide focus on .NET, but the WinApp CLI is designed to work across the Windows development ecosystem. Support for additional stacks is actively in progress:

- **Electron** — `winapp init` creates a `winapp.yaml` and manifest; the npm package (`winapp`) wraps the CLI with Electron-specific commands for sparse packaging and debug identity.
- **C++ / CMake** — `winapp init` downloads Windows SDK and Windows App SDK packages, generates C++/WinRT headers, and sets up build tools.
- **Flutter, Rust, Tauri** — same `winapp init` + `winapp pack` workflow, with technology-specific guidance in the [docs/guides](guides/) folder.

For full CLI reference, see [usage.md](usage.md). For LLM/agent integration, see [using-with-llms.md](using-with-llms.md).
