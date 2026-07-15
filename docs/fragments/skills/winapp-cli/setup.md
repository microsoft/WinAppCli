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

**`Package.appxmanifest`** is the most important file winapp creates — it declares your app's identity, capabilities, and visual assets. Most winapp commands require it (`package`, `run`, `cert generate --manifest`).

**`winapp.yaml`** is only needed for SDK version management via `restore`/`update`. Projects that already reference Windows SDK packages (e.g., via NuGet in a `.csproj`) can use winapp commands without it.

**`.winapp/`** is the local folder where SDK packages and generated projections (e.g., CppWinRT headers) are stored. This folder is `.gitignore`d — team members recreate it via `winapp restore`.

## Usage

### Initialize a new winapp project

```powershell
# Interactive — prompts for app name, publisher, SDK channel, etc.
# Automatically searches for compatible projects (Tauri, Electron, .NET, Rust, C++, Flutter)
winapp init

# Non-interactive — accepts all defaults (stable SDKs, current folder name as app name)
winapp init . --use-defaults

# Non-interactive with JS bindings enabled
winapp init . --use-defaults --add-js-bindings

# Skip SDK installation (just manifest + config)
winapp init . --use-defaults --setup-sdks none

# Install preview SDKs instead of stable
winapp init . --use-defaults --setup-sdks preview
```

After `init`, your project will contain:
- `Package.appxmanifest` — package identity and capabilities
- `Assets/` — default app icons (Square44x44Logo, Square150x150Logo, etc.)
- `winapp.yaml` — SDK version pinning for `restore`/`update`
- `.winapp/` — downloaded SDK packages and generated projections
- `.gitignore` update — excludes `.winapp/` and `devcert.pfx`

When JS bindings are enabled (via `--add-js-bindings` or by answering yes in interactive init), npm/Electron projects also get:
- `.winapp/bindings/` — generated JS bindings for Windows App SDK APIs (npm-only, Node / Electron)
- `package.json` update — adds the `winapp.jsBindings` namespace and `@microsoft/dynwinrt` dependency (npm-only)

### Restore after cloning

```powershell
# Reinstall SDK packages from existing winapp.yaml (does not change versions)
winapp restore

# Restore into a specific directory
winapp restore ./my-project
```

Use `restore` when you clone a repo that already has `winapp.yaml` but no `.winapp/` folder.

### Private or custom NuGet feeds

`init`, `restore`, and `update` download the Windows SDK and Windows App SDK packages through NuGet, honoring your standard `nuget.config` hierarchy (project, user, and machine level). To restore the SDK packages from an internal feed or mirror, add it under `<packageSources>`; winapp queries every enabled source — over HTTPS, or a local folder path — and picks the highest listed version for `init`/`update` (excluding versions the publisher has *unlisted* — except on a v3 feed that exposes only a flat-container `PackageBaseAddress` with no registration resource, which carries no listed/unlisted flag, so an unlisted version could be selected there; registration-backed feeds such as nuget.org and most Azure Artifacts feeds are unaffected). A plain-**HTTP** feed is refused unless it opts in with `allowInsecureConnections="true"` on the `<add>` entry (the SDK packages are executables, so an unencrypted feed is a code-substitution vector) — switch the mirror to HTTPS or set that attribute. If your `nuget.config` defines a `<packageSourceMapping>`, only the sources mapped to a given package are queried for it, so an enabled feed excluded by the mapping is skipped (and an unmapped package fails to resolve rather than falling back to another feed). To use *only* your feed, `<clear />` the inherited sources first:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contoso" value="https://pkgs.dev.azure.com/contoso/_packaging/winsdk-mirror/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

Authentication uses credentials from `nuget.config` (`<packageSourceCredentials>`), environment-based credentials, and NuGet credential-provider plugins automatically — interactive prompts only appear on interactive terminals, so CI relies on pre-configured/environment credentials. The package cache location follows `NUGET_PACKAGES` / `globalPackagesFolder`, falling back to `~/.nuget/packages`.

> **Security note:** winapp honors the `nuget.config` in the selected project/config directory — the working directory by default, or the directory passed to `init <dir>` / `restore --config-dir <dir>` — so it restores from the feeds (and into the `globalPackagesFolder`) that config specifies. Run `init`/`restore`/`update` only against directories you trust, the same as `dotnet restore`. Use `<packageSourceMapping>` to pin packages to specific feeds when more than one source is configured.

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
winapp run ./dist --manifest ./out/Package.appxmanifest --args "--my-flag value"

# Pass arguments after -- to avoid escaping (equivalent to --args)
winapp run ./bin/Debug -- --my-flag value

# Register identity without launching (useful for attaching a debugger manually)
winapp run ./bin/Debug --no-launch

# Launch and capture OutputDebugString messages and crash diagnostics
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

> **`--debug-output` caveat:** Captures `OutputDebugString` and crash diagnostics (minidump + automatic analysis for both managed and native crashes) but attaches winapp as the debugger — you cannot also attach VS Code or WinDbg. Use `--no-launch` if you need your own debugger. Add `--symbols` to download PDB symbols for richer native crash analysis. For WinUI 3 apps, a stowed-exception triage pass runs automatically (surfacing the originating HRESULT and native XAML dispatch stack); the debugger components it needs are downloaded on first use, or set `WINAPP_DBGTOOLS_DIR` to a directory containing `dbgeng.dll` and `JsProvider.dll` for offline/locked-down environments.

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Recommended workflow

1. **Initialize** — `winapp init . --use-defaults` in your existing project
2. **Configure** — edit `Package.appxmanifest` to add capabilities your app needs (e.g., `runFullTrust`, `internetClient`)
3. **Build** — build your app as usual (dotnet build, cmake, npm run build, etc.)
4. **Run with identity** — `winapp run ./bin/Debug` to register identity and launch for debugging
5. **Package** — `winapp package ./bin/Release --cert ./devcert.pfx` to create MSIX

## Tips

- Use `--use-defaults` (alias: `--no-prompt`) in CI/CD pipelines and scripts to avoid interactive prompts. Non-interactive environments (piped stdin, CI runners) are auto-detected and will use defaults automatically with a warning.
- If you only need `Package.appxmanifest` without SDK setup, use `winapp manifest generate` instead of `init`
- `winapp init` is idempotent for the config file — re-running it won't overwrite an existing `winapp.yaml` unless you use `--config-only`
- For Electron projects, prefer `npm install --save-dev @microsoft/winappcli` and use `npx winapp init` instead of the standalone CLI

## Related skills
- After setup, see `winapp-manifest` to customize your `Package.appxmanifest`
- Ready to package? See `winapp-package` to create an MSIX installer
- Need a certificate? See `winapp-signing` for certificate generation
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore`/`update` without config | Run `winapp init` first, or ensure you're in the right directory |
| "Directory not found" | Target directory doesn't exist | Create the directory first or check the path |
| SDK download fails | Network issue or firewall | Ensure internet access; check proxy settings |
| SDK download fails with 401/403 | Private feed requires authentication | Store credentials in `nuget.config` (`<packageSourceCredentials>`) or configure a credential provider / feed environment credentials before running in CI |
| SDK package not found on private feed | Feed doesn't mirror the SDK packages, or the wrong source is configured | Ensure the feed serves `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.CPP`, `Microsoft.Windows.CppWinRT`, etc.; keep `nuget.org` enabled if the feed only supplements it |
| `init` prompts unexpectedly in CI | Missing `--use-defaults` flag | Add `--use-defaults` to skip all prompts (note: non-interactive shells are now auto-detected) |
