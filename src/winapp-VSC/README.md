# WinApp — VS Code Extension

The **WinApp** extension brings the [Windows App Development CLI (WinApp CLI)](https://github.com/microsoft/WinAppCli) into Visual Studio Code so you can initialize, debug, package, and sign Windows applications without leaving the editor.

> **Status: Public Preview** — The WinApp CLI and this extension are experimental and in active development. We'd love your feedback! [File an issue](https://github.com/microsoft/WinAppCli/issues).

## Get Started

> [!IMPORTANT]
> The WinApp VS Code Extension is not yet available in the VS Code Marketplace. We plan to publish the extension publicly soon. 

Try the WinApp extension today by downloading our latest prerelease: [**VS Code Extension**](https://nightly.link/microsoft/WinAppCli/workflows/build-package/main/vscode-extension.zip)

Simply navigate to the 'Extensions' tab in VS Code, and select the option to 'Install via VSIX...'. You may need to restart VS Code for the extension to begin working. 

## Features

### Command Palette

All commands are accessible from the Command Palette (`Ctrl+Shift+P`). Type **WinApp** to see the full list.

| Command | Description |
|---------|-------------|
| **WinApp: Initialize Project** | Set up a new project with the Windows SDK and/or Windows App SDK. Prompts for SDK channel (stable, preview, experimental, or none). |
| **WinApp: Restore Packages** | Restore project packages and dependencies. |
| **WinApp: Update Packages** | Update packages and dependencies to the latest versions. |
| **WinApp: Run Application** | Run your app as a loose-layout packaged application with full package identity — great for testing APIs that require identity. |
| **WinApp: Create Debug Identity** | Add sparse package identity to an existing executable so you can launch and debug it directly from VS Code with identity. |
| **WinApp: Unregister Package** | Unregister a sideloaded development package (e.g., one registered via Run or Create Debug Identity). |
| **WinApp: Create MSIX Package** | Package your application into an MSIX, with options to generate a certificate and bundle the runtime self-contained. |
| **WinApp: Generate Manifest** | Generate an `AppxManifest.xml` from a template (packaged or sparse). |
| **WinApp: Add Manifest Execution Alias** | Add an execution alias to the manifest so the packaged app can be launched from the command line. |
| **WinApp: Update Manifest Assets** | Auto-generate all required app icon assets from a single source image (PNG, JPG, GIF, or BMP). |
| **WinApp: Generate Certificate** | Create a development certificate for signing, with an option to install it immediately. |
| **WinApp: Install Certificate** | Install an existing `.pfx` or `.cer` certificate. (requires Admin elevation) |
| **WinApp: Certificate Info** | Display certificate details (subject, thumbprint, expiry) to verify a certificate matches your manifest. |
| **WinApp: Sign Package** | Sign an MSIX package or executable with a certificate. |
| **WinApp: Run SDK Tool** | Run Windows SDK tools (`makeappx`, `signtool`, `mt`, `makepri`) with custom arguments. |
| **WinApp: Get WinApp Path** | Show paths to installed SDK components. |

### Integrated Debugging

The extension provides a **custom `winapp` debug type** that launches your app with package identity and automatically attaches the appropriate debugger — all from a single **F5** press.

**How it works:**

1. You press **F5** (or start a debug session).
2. The extension locates your build output directories (by scanning for `.exe` files) and optionally uses a manifest specified via `manifest` in `launch.json` or auto-detected by the CLI.
3. You'll then have the option to select the build directory you'd like to run.
4. It launches your app via `winapp run` to give it package identity.
5. A child debug session attaches to the running process using the debugger you specified.

> [!IMPORTANT]
> The `winapp` debug type assumes your project has already been built and that a build output folder containing an `.exe` exists in your project. It **does not** build your project automatically — so after making code changes, you must rebuild your project before launching to see those changes reflected in the running app.

> [!TIP]
> You can automate the build step by adding a `preLaunchTask` to your `launch.json` configuration. This tells VS Code to run a build task before every debug session, so your changes are always compiled before launch.
>
> 1. Define a build task in `.vscode/tasks.json` (example for .NET):
>    ```jsonc
>    {
>        "version": "2.0.0",
>        "tasks": [
>            {
>                "label": "build",
>                "command": "dotnet",
>                "type": "process",
>                "args": ["build", "${workspaceFolder}"],
>                "problemMatcher": "$msCompile"
>            }
>        ]
>    }
>    ```
> 2. Reference it in your `launch.json`:
>    ```jsonc
>    {
>        "type": "winapp",
>        "request": "launch",
>        "name": "WinApp: Launch and Attach",
>        "preLaunchTask": "build"
>    }
>    ```

**Supported debuggers:**

| `debuggerType` | Language | Required Extension |
|----------------|----------|--------------------|
| `coreclr` (default) | C# / .NET | [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp) |
| `cppvsdbg` | C / C++ | [C/C++](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools) |
| `node` | Node.js / Electron | Built-in |

**Example `launch.json`:**

```jsonc
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "winapp",
            "request": "launch",
            "name": "WinApp: Launch and Attach",
        }
    ]
}
```

**Configuration properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `inputFolder` | string | | Path to the build output folder containing your app binaries (e.g., `${workspaceFolder}/bin/Debug/net8.0-windows10.0.22621`). If not set, you will be prompted to select a folder. |
| `manifest` | string | | Path to the `AppxManifest.xml` file. If not set, the CLI auto-detects from the input folder or current directory. |
| `debuggerType` | string | `coreclr` | Underlying debugger to use (`coreclr`, `cppvsdbg`, or `node`). |
| `workingDirectory` | string | workspace folder | Working directory for the application. |
| `args` | string | | Command-line arguments to pass to the application. |
| `outputAppxDirectory` | string | | Output directory for the loose-layout package. Defaults to an `AppX` folder inside the input folder. |

## Scenarios

### Initialize and set up a project

Run **WinApp: Initialize Project** to configure your project with the Windows SDK and/or Windows App SDK. The command walks you through selecting an SDK channel and sets up the necessary dependencies.

### Debug with package identity

Many Windows APIs — notifications, background tasks, on-device AI, share targets — require your app to have **package identity**. The WinApp debug type gives your app identity automatically when you press F5, so you can test these APIs during development without building a full MSIX installer.

For scenarios where you need to debug startup code from the very first instruction, use **WinApp: Create Debug Identity** to register a sparse package for your executable, then launch it normally with your preferred debugger.

When you're done testing, use **WinApp: Unregister Package** to clean up sideloaded packages without leaving VS Code.

See the full [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md) for more details.

### Generate manifests and assets

Use **WinApp: Generate Manifest** to create an `Package.appxmanifest` from a template, then **WinApp: Update Manifest Assets** to auto-generate all required app icons from a single source image. Use **WinApp: Add Manifest Execution Alias** to add a command-line alias so your packaged app can be launched by typing its name in a terminal.

### Package and sign

Use **WinApp: Create MSIX Package** to package your application. Pair it with **WinApp: Generate Certificate** and **WinApp: Sign Package** to produce a signed, ready-to-distribute MSIX. Use **WinApp: Certificate Info** to verify a certificate's details (subject, thumbprint, expiry) before signing.

### Access Windows SDK tools

**WinApp: Run SDK Tool** gives you direct access to `makeappx`, `signtool`, `mt`, and `makepri` — no need to find SDK installation paths or open a separate Developer Command Prompt.

## Supported Frameworks

The winapp CLI (and this extension) works with any Windows app framework:

- **.NET** — WPF, WinForms, Console, WinUI 3
- **C / C++** — Win32, CMake, MSBuild
- **Electron** / **Node.js**
- **Rust**
- **Tauri**
- **Flutter**

## Requirements

- Windows 10 or later
- Visual Studio Code 1.109.0 or later

The winapp CLI is bundled with the extension — no separate installation required.

For debugging, install the debugger extension that matches your app's language (see [Supported debuggers](#integrated-debugging) above).

## Troubleshooting

| Problem | Cause | Solution |
|---------|-------|----------|
| **"No folders containing .exe files found in the workspace..."** or **"No build output folder selected..."** when pressing F5 | The project hasn't been built yet, or the build output is in an unexpected location. | Build your project first (e.g., `dotnet build`), or set `inputFolder` in `launch.json` to point to the folder containing your `.exe`. |
| **Debugger doesn't attach** | The required debugger extension isn't installed. | Install the matching extension for your language — see [Supported debuggers](#integrated-debugging). |
| **App launches but changes aren't visible** | The `winapp` debug type does not build the project automatically. | Rebuild your project before pressing F5, or add a `preLaunchTask` to automate it (see the tip in [Integrated Debugging](#integrated-debugging)). |
| **Certificate trust error when running** | The development certificate isn't installed or has expired. | Run **WinApp: Generate Certificate** and choose to install it, or run **WinApp: Install Certificate** with your existing `.pfx` file. (requires Admin elevation) |
| **"Access denied" or permission errors** | Some operations (certificate install, package registration) require elevation. | Run VS Code as Administrator, or use an elevated terminal for the failing command. |

## Feedback and Support

- [File an issue or feature request](https://github.com/microsoft/WinAppCli/issues)
- [Support Guide](https://github.com/microsoft/WinAppCli/blob/main/SUPPORT.md)
