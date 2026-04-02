---
name: winapp-setup
description: Set up a Windows app project for MSIX packaging, Windows SDK access, or Windows API usage. Use when adding Windows support to an Electron, .NET, C++, Rust, Flutter, or Tauri project, or restoring SDK packages after cloning.
version: 0.2.2
---
## When to use

Use this skill when:
- **Adding Windows platform support** to an existing project (Electron, .NET, C++, Rust, Flutter, Tauri, etc.)
- **Cloning a repo** that already uses winapp and need to restore SDK packages
- **Updating SDK versions** to get the latest Windows SDK or Windows App SDK

## Prerequisites

Install the winapp CLI before running any commands:

```powershell
# Via winget (recommended for non-Node projects)
winget install Microsoft.WinAppCli --source winget

# Via npm (recommended for Electron/Node projects — includes Node.js SDK)
npm install --save-dev @microsoft/winappcli
```

You need an **existing app project** — `winapp init` does **not** create new projects, it adds Windows platform files to your existing codebase.

> **Already have a `Package.appxmanifest`?** .NET projects that already have a packaging manifest (e.g., WinUI 3 apps or projects with an existing MSIX packaging setup) likely **don't need `winapp init`**. Ensure your `.csproj` references the `Microsoft.WindowsAppSDK` NuGet package and has the right properties for packaged builds (e.g., `<WindowsPackageType>MSIX</WindowsPackageType>`). WinUI 3 apps created from Visual Studio templates are typically already fully configured — you can go straight to building and using `winapp run` or `winapp package`.

## Key concepts

**`appxmanifest.xml`** is the most important file winapp creates — it declares your app's identity, capabilities, and visual assets. Most winapp commands require it (`package`, `run`, `cert generate --manifest`).

**`winapp.yaml`** is only needed for SDK version management via `restore`/`update`. Projects that already reference Windows SDK packages (e.g., via NuGet in a `.csproj`) can use winapp commands without it.

**`.winapp/`** is the local folder where SDK packages and generated projections (e.g., CppWinRT headers) are stored. This folder is `.gitignore`d — team members recreate it via `winapp restore`.

## Usage

### Initialize a new winapp project

```powershell
# Interactive — prompts for app name, publisher, SDK channel, etc.
winapp init .

# Non-interactive — accepts all defaults (stable SDKs, current folder name as app name)
winapp init --use-defaults

# Skip SDK installation (just manifest + config)
winapp init --use-defaults --setup-sdks none

# Install preview SDKs instead of stable
winapp init --use-defaults --setup-sdks preview
```

After `init`, your project will contain:
- `appxmanifest.xml` — package identity and capabilities
- `Assets/` — default app icons (Square44x44Logo, Square150x150Logo, etc.)
- `winapp.yaml` — SDK version pinning for `restore`/`update`
- `.winapp/` — downloaded SDK packages and generated projections
- `.gitignore` update — excludes `.winapp/` and `devcert.pfx`

### Restore after cloning

```powershell
# Reinstall SDK packages from existing winapp.yaml (does not change versions)
winapp restore

# Restore into a specific directory
winapp restore ./my-project
```

Use `restore` when you clone a repo that already has `winapp.yaml` but no `.winapp/` folder.

### Update SDK versions

```powershell
# Check for and install latest stable SDK versions
winapp update

# Switch to preview channel
winapp update --setup-sdks preview
```

This updates `winapp.yaml` with the latest versions and reinstalls packages.

### Run and debug with identity

```powershell
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and pass arguments to the app
winapp run ./dist --manifest ./out/AppxManifest.xml --args "--my-flag value"

# Register identity without launching (useful for attaching a debugger manually)
winapp run ./bin/Debug --no-launch

# Launch and capture OutputDebugString messages and first-chance exceptions
# Note: prevents other debuggers (VS, VS Code) from attaching — use --no-launch if you need those instead
winapp run ./bin/Debug --debug-output
```

Use `winapp run` during iterative development — it creates a loose layout package, registers a debug identity, and launches the app in one step. For identity-only registration without loose layout, use `winapp create-debug-identity` instead.

#### Choosing between `run` and `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **Registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **App launch** | Winapp launches via AUMID or alias | You launch the exe yourself |
| **Simulates MSIX** | Yes — closest to production | No — identity only |
| **Files** | Copied to AppX layout dir | Exe stays in place |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or F5 startup debugging |

**Default to `winapp run`.** Use `create-debug-identity` when you need your IDE to launch and debug the exe directly (startup debugging), or when the exe is separate from your source (Electron).

For console apps, add `--with-alias` to preserve stdin/stdout in the current terminal.

> **`--debug-output` caveat:** Captures `OutputDebugString` but attaches winapp as the debugger — you cannot also attach VS Code or WinDbg. Use `--no-launch` if you need your own debugger.

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Recommended workflow

1. **Initialize** — `winapp init --use-defaults` in your existing project
2. **Configure** — edit `appxmanifest.xml` to add capabilities your app needs (e.g., `runFullTrust`, `internetClient`)
3. **Build** — build your app as usual (dotnet build, cmake, npm run build, etc.)
4. **Run with identity** — `winapp run ./bin/Debug` to register identity and launch for debugging
5. **Package** — `winapp package ./bin/Release --cert ./devcert.pfx` to create MSIX

## Tips

- Use `--use-defaults` (alias: `--no-prompt`) in CI/CD pipelines and scripts to avoid interactive prompts
- If you only need `appxmanifest.xml` without SDK setup, use `winapp manifest generate` instead of `init`
- `winapp init` is idempotent for the config file — re-running it won't overwrite an existing `winapp.yaml` unless you use `--config-only`
- For Electron projects, prefer `npm install --save-dev @microsoft/winappcli` and use `npx winapp init` instead of the standalone CLI

## Related skills
- After setup, see `winapp-manifest` to customize your `appxmanifest.xml`
- Ready to package? See `winapp-package` to create an MSIX installer
- Need a certificate? See `winapp-signing` for certificate generation
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore`/`update` without config | Run `winapp init` first, or ensure you're in the right directory |
| "Directory not found" | Target directory doesn't exist | Create the directory first or check the path |
| SDK download fails | Network issue or firewall | Ensure internet access; check proxy settings |
| `init` prompts unexpectedly in CI | Missing `--use-defaults` flag | Add `--use-defaults` to skip all prompts |


## Command Reference

### `winapp init`

Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates appxmanifest.xml with default assets, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<base-directory>` | No | Base/root directory for the winapp workspace, for consumption or installation. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--config-dir` | Directory to read/store configuration (default: current directory) | (none) |
| `--config-only` | Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. | (none) |
| `--ignore-config` | Don't use configuration file for version management | (none) |
| `--no-gitignore` | Don't update .gitignore file | (none) |
| `--setup-sdks` | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) | (none) |
| `--use-defaults` | Do not prompt, and use default of all prompts | (none) |

### `winapp restore`

Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages from existing winapp.yaml without changing versions. Requires winapp.yaml (created by 'init'). To check for newer SDK versions, use 'update' instead.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<base-directory>` | No | Base/root directory for the winapp workspace |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--config-dir` | Directory to read configuration from (default: current directory) | (none) |

### `winapp update`

Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--setup-sdks` | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) | (none) |

### `winapp run`

Creates packaged layout, registers the Application, and launches the packaged application.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<input-folder>` | Yes | Input folder containing the app to run |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--args` | Command-line arguments to pass to the application | (none) |
| `--debug-output` | Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. Cannot be combined with --no-launch or --json. | (none) |
| `--json` | Format output as JSON | (none) |
| `--manifest` | Path to the appxmanifest.xml (default: auto-detect from input folder or current directory) | (none) |
| `--no-launch` | Only create the debug identity and register the package without launching the application | (none) |
| `--output-appx-directory` | Output directory for the loose layout package. If not specified, a directory named AppX inside the input-folder directory will be used. | (none) |
| `--unregister-on-exit` | Unregister the development package after the application exits. Only removes packages registered in development mode. | (none) |
| `--with-alias` | Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. | (none) |
