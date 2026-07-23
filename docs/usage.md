<!-- mslearn: true -->
<!-- ms.topic: reference -->
<!-- description: Complete command reference for the Windows App Development CLI (winapp CLI) including setup, packaging, identity, certificates, signing, and utility commands. -->
# CLI Documentation and Usage

## Shell Completion

Enable tab completion for commands, options, and values. See the [Shell Completion guide](guides/shell-completion.md) for setup instructions.

```powershell
# Quick setup for PowerShell (permanent — add to profile)
winapp complete --setup powershell >> $PROFILE

# Or try it in the current session only
winapp complete --setup powershell | Out-String | Invoke-Expression
```

### init

Initialize a directory with Windows SDK, Windows App SDK, and required assets for modern Windows development.

```bash
winapp init [base-directory] [options]
```

**Arguments:**

- `base-directory` - Base/root directory for the app/workspace (default: current directory)

**Options:**

- `--config-dir <path>` - Directory to read/store configuration (default: current directory)
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--ignore-config`, `--no-config` - Don't use configuration file for version management
- `--no-gitignore` - Don't update .gitignore file
- `--use-defaults`, `--no-prompt` - Do not prompt, and use default of all prompts
- `--config-only` - Only handle configuration file operations, skip package installation
- `--add-js-bindings` *(npm only)* - Add `winapp.jsBindings` to package.json and generate JS/TypeScript bindings, without prompting (incompatible with `--setup-sdks none`)

**What it does:**

- Creates `winapp.yaml` configuration file (only when SDK packages are managed; skipped with `--setup-sdks none`)
- Downloads Windows SDK and Windows App SDK packages
- Generates C++/WinRT headers and binaries
- Creates Package.appxmanifest
- Sets up build tools and enables developer mode
- Updates .gitignore to exclude generated files
- Stores shareable files in the global cache directory
- Generates JS bindings for Windows App SDK APIs when enabled (npm only)

**Automatic project detection:**

When `init` is run without a directory argument, it performs a breadth-first search of the current directory tree to find compatible projects (up to 10). Supported project types:

- **Tauri** — `tauri.conf.json` found one level below the directory
- **Electron** — `package.json` with `electron` in dependencies or devDependencies
- **Flutter** — `pubspec.yaml` at project root
- **.NET** — `.csproj` at project root
- **Rust** — `Cargo.toml` at project root
- **C++** — `CMakeLists.txt` at project root

The search skips commonly ignored directories (node_modules, bin, obj, .git, etc.). When a compatible project is found, subdirectories below it are not searched.

- If a directory argument is provided (e.g., `winapp init .` or `winapp init path/to/project`), the search is skipped and `init` checks only that directory for a compatible project
- If `--use-defaults` (or `--no-prompt`) is set without a directory argument, `init` skips the search and initializes the current directory non-interactively, warning first if no known project type is detected there (e.g., `winapp init --use-defaults`)
- In non-interactive environments (piped stdin, CI, redirected input), `init` automatically uses `--use-defaults` behavior and emits a warning: `Non-interactive environment detected. Using default values.`
- If the current directory is a compatible project, `init` proceeds immediately
- If exactly one project is found elsewhere, you're prompted to confirm
- If multiple projects are found, you can select which one to initialize — the current directory is always available as a fallback option
- If no projects are found, you're warned and asked whether to proceed anyway
- If the search reaches the 10-project limit, a warning suggests providing a directory argument

**Automatic .NET project flow:**

When a `.csproj` file is found in the target directory, `init` uses a streamlined .NET-specific flow:

- Validates and updates the `TargetFramework` to a Windows-compatible TFM (e.g., `net10.0-windows10.0.26100.0`)
- Adds `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` as NuGet `PackageReference` entries directly in the `.csproj`
- Generates `Package.appxmanifest`, assets, and a development certificate
- Does **not** create a `winapp.yaml` or download C++ projections (use `dotnet restore` for NuGet packages)

**Examples:**

```bash
# Initialize current directory
winapp init

# Initialize with experimental packages
winapp init --setup-sdks experimental

# Initialize specific directory without prompts
winapp init ./my-project --use-defaults

# Initialize a .NET project (auto-detected from .csproj)
cd my-dotnet-app
winapp init
```

**Tip: Install SDKs after initial setup**

If you ran `init` with `--setup-sdks none` (or skipped SDK installation) and later need the SDKs:

```bash
# Re-run init to install SDKs - preserves existing files (manifest, etc.)
winapp init . --use-defaults --setup-sdks stable
```

Use `--setup-sdks preview` or `--setup-sdks experimental` for preview/experimental SDK versions.

---

### restore

Restore packages and regenerate files based on existing `winapp.yaml` configuration.

```bash
winapp restore [options]
```

**Options:**

- `--config-dir <path>` - Directory containing winapp.yaml (default: current directory)

**What it does:**

- Reads existing `winapp.yaml` configuration
- Downloads/updates SDK packages to specified versions
- Regenerates C++/WinRT headers and binaries
- Stores shareable files in the global cache directory

> [!NOTE]
> For .NET projects initialized with `winapp init`, there is no `winapp.yaml`. Use `dotnet restore` to restore NuGet packages instead.

**Examples:**

```bash
# Restore from winapp.yaml in current directory
winapp restore
```

---

### update

Update packages to their latest versions and update the configuration file.

```bash
winapp update [options]
```

**Options:**

- `--setup-sdks <stable|preview|experimental|none>` - SDK installation mode: `stable` (default), `preview`, `experimental`, or `none` (skip SDK installation)

**What it does:**

- Reads existing `winapp.yaml` configuration in the current directory
- Updates all packages to their latest available versions
- Updates the `winapp.yaml` file with new version numbers
- Regenerates C++/WinRT headers and binaries

**Examples:**

```bash
# Update packages to latest versions
winapp update

# Update including experimental packages
winapp update --setup-sdks experimental
```

---

### pack

Create MSIX packages from prepared application directories. Requires a manifest file (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported) to be present in the target directory, in the current directory, or passed with the `--manifest` option. (run `init` or `manifest generate` to create a manifest)

Pass multiple input folders to create an `.msixbundle` for multi-architecture distribution (see [Multi-architecture bundles](#multi-architecture-bundles) below).

```bash
winapp pack <input-folder> [input-folder...] [options]
```

**Arguments:**

- `input-folder` - One or more directories containing the application files to package. Pass multiple folders (e.g., `./publish/x64 ./publish/arm64`) to create an MSIX bundle.

**Options:**

- `--output <filename>` - Output file name. For single packages: `<name>_<version>_<arch>.msix` (falling back to `<name>_<version>.msix`, `<name>_<arch>.msix`, or `<name>.msix`). For bundles: `<name>_<version>_<arch1>_<arch2>.msixbundle`.
- `--name <name>` - Package name (default: from manifest)
- `--manifest <path>` - Path to manifest file (`Package.appxmanifest` preferred, `appxmanifest.xml` also supported; default: auto-detect)
- `--cert <path>` - Path to signing certificate (enables auto-signing)
- `--cert-password <password>` - Certificate password (default: "password")
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--publisher <name>` - Publisher for certificate generation. Accepts a full X.500 distinguished name or a bare name (automatically wrapped as `CN=<name>`)
- `--self-contained` - Bundle Windows App SDK runtime
- `--skip-pri` - Skip PRI file generation
- `--executable <path>` - Path to the executable relative to the input folder (also `--exe`). Used to resolve `$targetnametoken$` placeholders in the manifest.

**What it does:**

- Validates and processes Package.appxmanifest files
- Resolves `$placeholder$` tokens in the manifest (see [Manifest placeholders](#manifest-placeholders) below)
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Automatically discovers and bundles any non-image files referenced in the manifest (e.g., AppExtension `manifest.json`, config files) from the manifest directory or input folder if they are missing from staging
- Automatically discovers third-party WinRT components and registers their activatable classes (see [WinRT component discovery](#winrt-component-discovery) below)
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

#### WinRT component discovery

When packaging, `winapp pack` automatically scans NuGet packages defined in the `winapp.yaml` or `*.csproj` for third-party WinRT components (e.g., Win2D). It parses `.winmd` files to extract activatable class names and locates their implementation DLLs. The discovered entries are registered as follows:

- **Framework-dependent** (default): Activatable classes are added as `<InProcessServer>` entries in the `Package.appxmanifest`
- **Self-contained** (`--self-contained`): Activatable classes are embedded in side-by-side (SxS) manifests within the executable

**Placeholder resolution during packaging:**

If the manifest contains `$targetnametoken$` in the `Executable` attribute:
1. If `--executable` is provided (path relative to the input folder), the placeholder is replaced with the specified value
2. Otherwise, `winapp pack` scans the input folder root for `.exe` files — if exactly one is found, it is used automatically
3. If zero or multiple `.exe` files are found, an error is shown asking you to specify `--executable`

**Examples:**

```bash
# Package directory with auto-detected manifest
winapp pack ./dist

# Package with custom output name and certificate
winapp pack ./dist --output MyApp.msix --cert ./cert.pfx

# Package with generated and installed certificate and self-contained WinAppSDK runtime
winapp pack ./dist --generate-cert --install-cert --self-contained

# Package with explicit executable (resolves $targetnametoken$ in manifest)
winapp pack ./dist --executable MyApp.exe
```

#### Multi-architecture bundles

When multiple input folders are passed, `winapp pack` creates an `.msixbundle` containing one `.msix` per architecture:

```bash
# Create unsigned bundle for Microsoft Store submission
winapp pack ./publish/x64 ./publish/arm64

# Create signed bundle for sideloading
winapp pack ./publish/x64 ./publish/arm64 --cert ./devcert.pfx

# Self-contained bundle
winapp pack ./publish/x64 ./publish/arm64 --self-contained --generate-cert
```

The command auto-detects each folder's architecture from the primary executable's PE header, validates consistency across slices (Identity, Capabilities, Dependencies), and produces a `<Name>_<Version>_<arch1>_<arch2>.msixbundle`.

**Manifest resolution for bundles:**

Each slice in the bundle needs a manifest. The command resolves manifests in this order:

1. **`--manifest <path>`** — If specified, this single manifest is used for all slices. The `ProcessorArchitecture` is automatically updated per-slice to match the detected architecture.

2. **Per-folder manifest** — If each input folder contains a `Package.appxmanifest` (or `appxmanifest.xml`), that folder's manifest is used for its slice.

3. **Current directory fallback** — If a folder has no manifest, the command looks for `Package.appxmanifest` in the current working directory and uses it (with architecture auto-stamped).

In all cases, the manifest is automatically updated: placeholders are resolved, dependencies are injected, and the `ProcessorArchitecture` is force-set to the detected architecture. After resolution, a cross-slice validation ensures that Identity (Name, Version, Publisher), Capabilities, and Dependencies are consistent across all slices — only `ProcessorArchitecture` may differ.
The package version defined in the slices is atributed to the MSIX bundle version, except if it's `0.0.0.0`, in which case a timestamp-based version is automatically generated.

```bash
# Option 1: Single shared manifest (simplest for most projects)
# Place Package.appxmanifest in your project root and run from there
winapp pack ./publish/x64 ./publish/arm64

# Option 2: Explicit manifest path
winapp pack ./publish/x64 ./publish/arm64 --manifest ./src/Package.appxmanifest

# Option 3: Per-folder manifests (useful if slices have different app extensions)
# Each folder already contains its own Package.appxmanifest
winapp pack ./publish/x64 ./publish/arm64
```

---

### create-debug-identity

Create app identity for debugging using [sparse packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps). The exe stays in its original location — Windows associates identity with it via `Add-AppxPackage -ExternalLocation`.

> **When to use this vs `winapp run`:** Use `create-debug-identity` when the exe is **separate from your app code** (e.g., Electron apps where `electron.exe` is in `node_modules`), or when specifically testing sparse package behavior. For most frameworks where the exe is in your build output folder, use [`winapp run`](#run) instead — it registers a full loose layout package and launches the app. See the [Debugging Guide](debugging.md) for a full comparison.

```bash
winapp create-debug-identity [entrypoint] [options]
```

**Arguments:**

- `entrypoint` - Path to executable (.exe) or script that needs identity

**Options:**

- `--manifest <path>` - Path to the app manifest file, either `Package.appxmanifest` or `appxmanifest.xml` (default: auto-detect `Package.appxmanifest` or `appxmanifest.xml` in the current directory)
- `--no-install` - Don't install the package after creation
- `--keep-identity` - Keep the manifest identity as-is, without appending `.debug` to the package name and application ID

**What it does:**

- Modifies executable's side-by-side manifest
- Registers sparse package for identity
- Enables debugging of identity-requiring APIs

**Examples:**

```bash
# Add identity to executable using local manifest
winapp create-debug-identity ./bin/MyApp.exe

# Add identity with custom manifest location
winapp create-debug-identity ./dist/app.exe --manifest ./custom-manifest.xml

# Create identity for hosted app script
winapp create-debug-identity app.py
```

---

### manifest

Generate and manage Package.appxmanifest files.

#### manifest generate

Generate Package.appxmanifest from templates.

```bash
winapp manifest generate [directory] [options]
```

**Arguments:**

- `directory` - Directory to generate manifest in (default: current directory)

**Options:**

- `--package-name <name>` - Package name (default: folder name)
- `--publisher-name <name>` - Publisher distinguished name (default: CN=\<current user\>). Accepts any valid X.500 DN; bare names are auto-wrapped as CN=\<name\>.
- `--version <version>` - Version (default: "1.0.0.0")
- `--description <text>` - Description (default: "My Application")
- `--entrypoint <path>` - Entry point executable or script
- `--template <type>` - Template type: `packaged` (default) or `sparse`
- `--logo-path <path>` - Path to logo image file
- `--if-exists <Error|Overwrite|Skip>` - Behavior when the manifest file already exists at the target path (default: `Error`)

**Templates:**

- `packaged` - Standard packaged app manifest
- `sparse` - App manifest using [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)

#### Manifest placeholders

Generated manifests use `$placeholder$` tokens (dollar-sign delimited) that are resolved automatically at packaging time:

| Placeholder | Resolved to | Example |
|-------------|-------------|---------|
| `$targetnametoken$` | Executable name without extension | `Executable="$targetnametoken$.exe"` &rarr; `Executable="MyApp.exe"` |
| `$targetentrypoint$` | `Windows.FullTrustApplication` | Always resolved automatically |

This follows the same convention used by Visual Studio project templates, so manifests are portable across tooling.

**How placeholders are resolved:**

- **`winapp pack`** — During packaging, `$targetnametoken$` is resolved using the `--executable` option or by auto-detecting the single `.exe` in the input folder. If multiple (or zero) `.exe` files are found and `--executable` is not specified, an error is shown.
- **`winapp create-debug-identity`** — When an entrypoint argument is provided, `$targetnametoken$` is resolved from it. Without an entrypoint, the executable placeholder must already be resolved in the manifest.
- **`winapp manifest generate --executable`** — When `--executable` is provided, manifest metadata (version, description) and icons are extracted from the executable, but the generated manifest still uses `$targetnametoken$.exe`; this placeholder is resolved later (e.g. `winapp pack` or `winapp create-debug-identity`).

> **PS:** Keeping `$targetnametoken$` in your checked-in manifest avoids hard-coding executable names and works with both `winapp pack` and Visual Studio builds.

**Examples:**

```bash
# Generate standard manifest interactively
winapp manifest generate

# Generate with all options specified
winapp manifest generate ./src --package-name MyApp --publisher-name "CN=My Company" --if-exists overwrite
```

#### manifest add-alias

Add an execution alias (`uap5:AppExecutionAlias`) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name.

```bash
winapp manifest add-alias [options]
```

**Options:**

- `--name <alias>` - Alias name (e.g. `myapp.exe`). Default: inferred from the `Executable` attribute in the manifest.
- `--manifest <path>` - Path to Package.appxmanifest (default: search current directory)
- `--app-id <id>` - Application Id to add the alias to (default: first Application element)

**What it does:**

- Reads the manifest and infers the alias from the `Executable` attribute (preserving placeholders like `$targetnametoken$.exe`)
- Adds the `uap5` namespace declaration if not already present
- Adds an `<Extensions>` block with `<uap5:AppExecutionAlias>` inside the target Application element
- If the alias already exists, reports it and exits successfully

**Examples:**

```bash
# Add alias inferred from Executable attribute (e.g. $targetnametoken$.exe)
winapp manifest add-alias

# Add alias with explicit name
winapp manifest add-alias --name myapp.exe

# Add alias to specific manifest
winapp manifest add-alias --manifest ./dist/Package.appxmanifest
```

#### manifest update-assets

Generate all required MSIX image assets from a single source image.

```bash
winapp manifest update-assets <image-path> [options]
```

**Arguments:**

- `image-path` - Path to source image file (PNG, JPG, SVG, ICO, GIF, BMP, etc.)

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest file (default: search current directory)
- `--light-image <path>` - Path to a separate source image for light theme variants

**Description:**

Takes a single source image and generates a comprehensive set of MSIX image assets based on the manifest's asset references:

For each asset referenced in the manifest:
- **5 scale variants** — base (no suffix), `.scale-125`, `.scale-150`, `.scale-200`, `.scale-400`

For the app icon (Square44x44Logo / AppList, 44×44 base):
- **14 plated targetsize variants** — `.targetsize-{16,20,24,30,32,36,40,48,60,64,72,80,96,256}`
- **14 unplated targetsize variants** — `.targetsize-{size}_altform-unplated`

Additionally:
- **app.ico** — Multi-resolution ICO file (16, 24, 32, 48, 256) for shell integration. If an existing `.ico` file is found in the assets directory (e.g. `AppIcon.ico` from a project template), it is replaced in-place rather than creating a duplicate

With `--light-image`:
- **Light theme targetsize variants** — `.targetsize-{size}_altform-lightunplated` (app icon)
- **Light theme scale variants** — `.scale-{factor}_altform-colorful_theme-light` (tiles, store logo)

**SVG support:** SVG files are fully supported as source images. They are rendered as vectors directly at each target size, producing pixel-perfect results at all resolutions.

The command scales images proportionally while maintaining aspect ratio, centering them with transparent backgrounds when needed. Assets are saved to the `Assets` directory relative to the manifest location.

**Examples:**

```bash
# Generate assets with auto-detected manifest
winapp manifest update-assets mylogo.png

# Use an SVG source for best quality at all sizes
winapp manifest update-assets mylogo.svg

# Specify manifest location explicitly
winapp manifest update-assets mylogo.png --manifest ./dist/Package.appxmanifest

# Generate light theme variants from a separate image
winapp manifest update-assets mylogo.png --light-image mylogo-light.png

# Use the same image for both (generates all MRT light theme qualifiers)
winapp manifest update-assets mylogo.png --light-image mylogo.png

# With verbose output
winapp manifest update-assets mylogo.png --verbose
```

---

### run

Create a loose layout package from a build output folder, register it with Windows using the `Windows.Management.Deployment.PackageManager` API, and launch the application — simulating a full MSIX install for debugging. Returns the process ID for debugger attachment.

> **This is the preferred command for debugging with package identity** for most frameworks (.NET, C++, Rust, Flutter, Tauri). Unlike [`create-debug-identity`](#create-debug-identity) which registers a sparse package for a single exe, `winapp run` registers the entire folder as a loose layout package, just like a real MSIX install. See the [Debugging Guide](debugging.md) for common debugging workflows.

```bash
winapp run <input-folder> [options]
```

**Arguments:**

- `input-folder` - Directory containing the app to run (required)

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from input folder or current directory)
- `--output-appx-directory <path>` - Output directory for the loose layout package (default: `AppX` inside the input folder directory)
- `--args <string>` - Command-line arguments to pass to the application. Alternatively, use `--` followed by arguments to avoid escaping (e.g., `winapp run . -- --flag value`).
- `--no-launch` - Only create the debug identity and register the package without launching the application
- `--with-alias` - Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a `uap5:ExecutionAlias` in the manifest (use `winapp manifest add-alias` to add one). Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
- `--debug-output` - Capture `OutputDebugString` messages and first-chance exceptions from the launched application. Framework noise (WinUI, COM, DirectX) is filtered from console output; the full log file captures everything. If the app crashes, automatically captures a minidump and analyzes it to show the exception type, message, and stack trace with source file:line numbers (resolved from PDBs in the build output folder). Managed (.NET) crashes are analyzed instantly with no external tools. Native (C++/WinRT) crashes show module names and offsets. When the crashed app is a WinUI 3 app (`Microsoft.UI.Xaml.dll` is loaded), an extra stowed-exception triage pass runs automatically to surface the originating HRESULT, its ErrorContext chain, and the full native XAML dispatch stack; the required debugger components are downloaded on first use (see [Debugging](debugging.md#winui-stowed-exception-triage), overridable via the `WINAPP_DBGTOOLS_DIR` environment variable). Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use `--no-launch` instead if you need to attach a different debugger. Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
- `--symbols` - Download PDB symbols from Microsoft Symbol Server for richer native crash analysis with resolved function names. Only used with `--debug-output`. If omitted and a native crash occurs, the output will suggest adding this flag. This flag also improves the WinUI stowed-exception triage stack for WinUI 3 apps. First run downloads symbols and caches them locally; subsequent runs use the cache.
- `--unregister-on-exit` - Unregister the development package after the application exits. Only removes packages registered in development mode. Cannot be combined with `--no-launch`.
- `--detach` - Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with `--json`). Cannot be combined with `--no-launch`, `--debug-output`, `--with-alias`, or `--unregister-on-exit`.
- `--clean` - Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments.
- `--json` - Format output as JSON for programmatic consumption (e.g. CI/automation). Useful with `--detach` to capture the PID. Cannot be combined with `--with-alias` or `--debug-output`.

**Application data persistence:**

By default, `winapp run` preserves your application's data (`LocalState`, `RoamingState`, `Settings`, etc.) when re-deploying. If your app writes data to `ApplicationData.Current.LocalFolder` or `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` within the package context, that data will survive across `winapp run` invocations.

Use `--clean` when you need a fresh start (e.g., to reset corrupted state or test first-run behavior).

**What it does:**

- Locates or generates the Package.appxmanifest
- Creates and registers a debug identity using a loose layout package
- Computes the Application User Model ID (AUMID)
- Launches the application using the registered identity (unless `--no-launch` is specified)
- Prints the process ID (PID) for debugger attachment

**Examples:**

```bash
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and arguments
winapp run ./dist --manifest ./out/Package.appxmanifest --args "--my-flag value"

# Pass arguments after -- to avoid escaping (equivalent to --args)
winapp run ./bin/Debug -- --my-flag value

# Specify output directory for loose layout package
winapp run ./bin/Release --output-appx-directory ./AppXDebug

# Register identity without launching
winapp run ./bin/Debug --no-launch

# Launch via execution alias (console apps run in current terminal)
winapp run ./bin/Debug --with-alias

# Launch and capture OutputDebugString messages and crash diagnostics
winapp run ./bin/Debug --debug-output

# Download native symbols for richer crash analysis (C++/WinRT crashes)
winapp run ./bin/Debug --debug-output --symbols

# Combine with execution alias to debug console apps inline
winapp run ./bin/Debug --with-alias --debug-output

# Run and automatically clean up registration on exit
winapp run ./bin/Debug --with-alias --unregister-on-exit

# Launch and detach immediately (useful for CI/automation)
winapp run ./bin/Debug --detach

# Detach with JSON output (returns PID for scripting)
winapp run ./bin/Debug --detach --json

# Wipe application data (LocalState, settings) and start fresh
winapp run ./bin/Debug --clean
```

**MSBuild properties (NuGet package):**

When using the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, `dotnet run` automatically invokes `winapp run`. The following MSBuild properties can be set in your `.csproj` to control behavior:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableWinAppRunSupport` | `true` | Enable/disable the run support functionality |
| `WinAppLaunchArgs` | (empty) | Arguments to pass to the app on launch |
| `WinAppRunUseExecutionAlias` | `false` | Launch via execution alias instead of AUMID activation |
| `WinAppRunNoLaunch` | `false` | Only register identity without launching |
| `WinAppRunDebugOutput` | `false` | Capture `OutputDebugString` messages and first-chance exceptions. Only one debugger can attach at a time (prevents VS/VS Code). Use `WinAppRunNoLaunch` instead to attach a different debugger. |

```xml
<PropertyGroup>
  <WinAppRunUseExecutionAlias>true</WinAppRunUseExecutionAlias>
  <WinAppRunDebugOutput>true</WinAppRunDebugOutput>
</PropertyGroup>
```

---

### unregister

Unregister a sideloaded development package. Only removes packages that were registered in development mode (e.g., via `winapp run` or `create-debug-identity`). Store-installed or MSIX-installed packages are never removed.

```bash
winapp unregister [options]
```

**Options:**

- `--manifest <path>` - Path to Package.appxmanifest (default: auto-detect from current directory)
- `--force` - Skip the install-location directory check and unregister even if the package was registered from a different project tree
- `--json` - Format output as JSON

**What it does:**

- Reads the package name from the manifest
- Searches for both `{name}` and `{name}.debug` packages (the debug variant is created by `create-debug-identity`)
- Verifies each package was registered in development mode (`IsDevelopmentMode == true`)
- Verifies the package's install location is under the current directory tree (unless `--force`)
- Unregisters matching packages

**Examples:**

```bash
# Unregister from current directory (auto-detects manifest)
winapp unregister

# Unregister with explicit manifest
winapp unregister --manifest ./Package.appxmanifest

# Force unregister even if registered from a different project tree
winapp unregister --force

# JSON output for scripting
winapp unregister --json
```

---

### cert

Generate, inspect, and install development certificates.

#### cert generate

Generate development certificates for package signing.

```bash
winapp cert generate [options]
```

**Options:**

- `--manifest <Package.appxmanifest>` - Extract publisher information from Package.appxmanifest 
- `--publisher <name>` - Publisher for the certificate. Accepts a full X.500 distinguished name (e.g., `CN=Contoso, O=Contoso Ltd, C=US`) or a bare name which is automatically wrapped as `CN=<name>`
- `--output <path>` - Output certificate file path (supports absolute and relative paths)
- `--password <password>` - Certificate password (default: "password")
- `--valid-days <valid-days>` - Number of days the certificate is valid (default: 365)
- `--install` - Install the certificate to the local machine store after generation
- `--if-exists <Error|Overwrite|Skip>` - Set behavior if the certificate file already exists (default: Error)
- `--export-cer` - Export a `.cer` file (public key only) alongside the `.pfx`. Useful for distributing the public certificate separately for trust installation.
- `--json` - Format output as JSON for programmatic consumption. Errors are also returned as JSON (`{"error": "..."}`).

#### cert info

Display certificate details from a PFX file. Useful for verifying a certificate matches your manifest before signing.

```bash
winapp cert info <cert-path> [options]
```

**Arguments:**

- `cert-path` - Path to the certificate file (PFX)

**Options:**

- `--password <password>` - Password for the PFX file (default: "password")
- `--json` - Format output as JSON

#### cert install

Install certificate to machine certificate store.

```bash
winapp cert install <cert-path> [options]
```

**Arguments:**

- `cert-path` - Path to certificate file to install

**Examples:**

```bash
# Generate certificate for specific publisher
winapp cert generate --publisher "CN=My Company" --output ./mycert.pfx

# Generate certificate and export public key .cer file
winapp cert generate --publisher "CN=My Company" --export-cer

# Generate certificate with JSON output (for scripting)
winapp cert generate --publisher "CN=My Company" --json

# View certificate details
winapp cert info ./mycert.pfx

# View certificate details as JSON
winapp cert info ./mycert.pfx --json

# Install certificate to machine
winapp cert install ./mycert.pfx
```

---

### sign

Sign MSIX packages and executables with certificates.

```bash
winapp sign <file-path> [options]
```

**Arguments:**

- `file-path` - Path to MSIX package or executable to sign

**Options:**

- `--cert <path>` - Path to signing certificate
- `--cert-password <password>` - Certificate password (default: "password")

**Examples:**

```bash
# Sign MSIX package
winapp sign MyApp.msix --cert ./mycert.pfx

# Sign executable
winapp sign ./bin/MyApp.exe --cert ./mycert.pfx --cert-password mypassword
```

---

### az-sign

Code-sign a file (exe, MSIX, or MSIX bundle) using [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) — a cloud-managed signing identity, so no private key (PFX) ever lives on the local machine.

```bash
winapp az-sign <file-path> [options]
```

**Arguments:**

- `file-path` - Path to the file to sign (exe, msix, or msixbundle)

**Options:**

- `--subscription`, `-s` - Azure subscription ID. If omitted and multiple exist, you are prompted
- `--resource-group`, `-r` - Resource group to narrow down signing accounts
- `--account` - Signing account name. Must be used with `--resource-group`
- `--profile`, `-p` - Certificate profile name. Must be used with `--account`
- `--metadata-file`, `-m` - Path to an existing `metadata.json`. Skips all prompting and signs directly

**Authentication:**

`az-sign` uses Azure's standard credential chain (`DefaultAzureCredential`). For CI/CD, set `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET` (or use GitHub Actions OIDC / managed identity). Interactively, if no credentials are found and the Azure CLI is installed, `az-sign` runs `az login` for you.

**Prerequisites:**

- An Azure Code Signing account and a certificate profile (created in the Azure portal after identity validation), plus the **Code Signing Certificate Profile Signer** role assigned to your identity.
- A machine-wide **x64 .NET 8 (or later) runtime** installed. The Azure signing client library is a managed assembly that `signtool.exe` loads in a separate process; winapp's own self-contained runtime does not satisfy it. Install it from https://dotnet.microsoft.com/download if signing fails with a runtime-load error.
- **SignTool 10.0.22621.755 or later** (from the Windows SDK). winapp installs SDK build tools automatically, but an older pinned/cached SDK may need updating.

**Examples:**

```bash
# Interactive — discover/select subscription, account, and profile
winapp az-sign ./app.msix

# Fully specified — no prompting (ideal for CI/CD)
winapp az-sign ./app.msix --subscription <sub-id> --resource-group <rg> --account <account> --profile <profile>

# Reuse an existing metadata.json (skips discovery and prompting)
winapp az-sign ./app.msix --metadata-file ./metadata.json
```

---

### create-external-catalog

Generate a `CodeIntegrityExternal.cat` catalog file containing hashes of executable files from specified directories. This catalog is used with the [TrustedLaunch](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-trustedlaunch-trustedlaunch) flag in MSIX sparse package manifests ([AllowExternalContent](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-uap10-allowexternalcontent)) to allow execution of external files not included in the package itself.

This is similar to how `signtool.exe` creates `AppxMetadata\CodeIntegrity.cat` when signing an MSIX package, but generates an external catalog for use with [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps).

```bash
winapp create-external-catalog <input-folder> [options]
```

**Arguments:**

- `input-folder` - One or more directories containing executable files to process. Separate multiple directories with semicolons (e.g., `"dir1;dir2"`)

**Options:**

- `--recursive`, `-r` - Include files from subdirectories
- `--use-page-hashes` - Include page hashes when generating the catalog (produces a larger catalog with per-page hash data)
- `--compute-flat-hashes` - Include flat file hashes when generating the catalog
- `--if-exists <Error|Overwrite|Skip>` - Behavior when the output file already exists (default: `Error`)
- `--output`, `-o` - Output catalog file path. If not specified, `CodeIntegrityExternal.cat` is created in the current directory. If a directory is specified, the default filename is appended.

**What it does:**

- Scans specified directories for executable files (PE binaries with code sections)
- Generates a Catalog Definition File (CDF) with hashes of all found executables
- Uses Windows CryptoCAT APIs to produce the `.cat` catalog file
- Non-executable files (e.g., `.txt`, `.dll` without code sections) are automatically skipped

**Examples:**

```bash
# Generate catalog for all executables in a directory
winapp create-external-catalog ./bin

# Include files in subdirectories
winapp create-external-catalog ./bin --recursive

# Specify a custom output path
winapp create-external-catalog ./bin --output ./dist/CodeIntegrityExternal.cat

# Overwrite existing catalog
winapp create-external-catalog ./bin --if-exists Overwrite

# Skip generation if catalog already exists
winapp create-external-catalog ./bin --if-exists Skip

# Include page hashes (for stricter code integrity validation)
winapp create-external-catalog ./bin --use-page-hashes

# Process multiple directories
winapp create-external-catalog "./bin;./lib" --recursive

# Combine multiple options
winapp create-external-catalog ./bin --recursive --use-page-hashes --compute-flat-hashes --output ./dist/CodeIntegrityExternal.cat --if-exists Overwrite
```

**When to use:**

Use this command when building a sparse MSIX package that uses TrustedLaunch to verify external executables. The typical workflow is:

1. `winapp manifest generate --template sparse` — Create a sparse manifest with `AllowExternalContent`
2. `winapp create-external-catalog ./bin` — Generate the code integrity catalog for your app's executables  
3. `winapp pack` — Package the manifest, assets, and catalog into an MSIX

---

### tool

Access Windows SDK tools directly. Uses tools available in [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/)

```bash
winapp tool <tool-name> [tool-arguments]
```

**Available tools:**

- `makeappx` - Create and manipulate app packages
- `signtool` - Sign files and verify signatures
- `mt` - Manifest tool for side-by-side assemblies
- And other Windows SDK tools from [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/)

**Examples:**

```bash
# Use signtool to verify signature
winapp tool signtool verify /pa MyApp.msix
```

---

### store

Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the [Microsoft Store Developer CLI](https://aka.ms/msstoredevcli).

```bash
winapp store [args...]
```

**Arguments:**

- `args...` – Arguments to pass directly to the `msstore` CLI. See [MSStore CLI documentation](https://aka.ms/msstoredevcli/docs) for available commands and options.

**What it does:**

- Ensures the Microsoft Store Developer CLI (`msstore`) is downloaded and available on your system.
- Forwards all arguments to the `msstore` CLI.
- Runs the command showing output directly in your terminal.

**Examples:**

```bash
# List all apps in your Microsoft Partner Center account
winapp store app list

# Publish a package to the Microsoft Store
winapp store publish ./myapp.msix --appId <your-app-id>
```

---

### get-winapp-path

Get paths to installed Windows SDK components.

```bash
winapp get-winapp-path [options]
```

**What it returns:**

- Paths to `.winapp` workspace directory
- Package installation directories
- Generated header locations

---

### node generate-bindings

*(Available in NPM package only)* Generate JS bindings for Windows App SDK APIs. The bindings are declared by a `"winapp": { "jsBindings": {...} }` namespace in **`package.json`** and written to `.winapp/bindings/`.

```bash
npx winapp node generate-bindings [options]
```

**Options:**

- `--verbose`, `-v` - Enable verbose per-file codegen output
- `--quiet`, `-q` - Suppress progress and informational output

**What it does:**

- Reads the `winapp.jsBindings` block from `package.json` and the `winmds.lock.json` written by the last `winapp restore`, then emits typed `.js` + `.d.ts` bindings into `.winapp/bindings/`
- Does **not** modify `package.json` — it is a passive regenerator. Adding the `winapp.jsBindings` block and the `@microsoft/dynwinrt` runtime dependency happens during [`winapp init`](#init) when JS bindings are enabled; this command fails fast if the block is absent
- Warns (but does not write) if `@microsoft/dynwinrt` is missing from your dependencies — run `npm install` after `init` has added it

> [!NOTE]
> Bindings are **npm-only** — they require invocation via `npx winapp` (the `@microsoft/winappcli` npm package); the standalone winget CLI does not surface them. Run [`winapp init`](#init) interactively and opt in, or use `winapp init . --use-defaults --add-js-bindings`, before using this command to regenerate bindings. If you edit `winapp.yaml`, run `npx winapp restore` to refresh Windows dependencies before regenerating.

**Examples:**

```bash
# Regenerate JS bindings in the current project
npx winapp node generate-bindings

# Regenerate after editing winapp.jsBindings, with verbose output
npx winapp node generate-bindings --verbose
```

> See the [JS bindings guide](guides/electron/js-file-picker.md) for the end-to-end workflow and the `winapp.jsBindings` configuration options.

---

### node create-addon

*(Available in NPM package only)* Generate native C++ or C# addon templates with Windows SDK and Windows App SDK integration.

```bash
npx winapp node create-addon [options]
```

**Options:**

- `--name <name>` - Addon name (default: "nativeWindowsAddon")
- `--template` - Select type of addon. Options are `cs` or `cpp` (default: `cpp`)
- `--verbose` - Enable verbose output

**What it does:**

- Creates addon directory with template files
- Generates binding.gyp and addon.cc with Windows SDK examples
- Installs required npm dependencies (nan, node-addon-api, node-gyp)
- Adds build script to package.json

**Examples:**

```bash
# Generate addon with default name
npx winapp node create-addon

# Generate custom named addon
npx winapp node create-addon --name myWindowsAddon
```

---

### node add-electron-debug-identity

*(Available in NPM package only)* Add app identity to Electron development process by using sparse packaging. Requires a Package.appxmanifest (create one with `winapp init` or `winapp manifest generate` if you don't have one).

> [!IMPORTANT]  
> There is a known issue with sparse packaging Electron applications which causes the app to crash on start or not render the web content. The issue has been fixed in Windows but it has not propagated to external Windows devices yet. If you are seeing this issue after calling `add-electron-debug-identity`, you can [disable sandboxing in your Electron app](https://www.electronjs.org/docs/latest/tutorial/sandbox#disabling-chromiums-sandbox-testing-only) for debug purposes with the `--no-sandbox` flag. This issue does not affect full MSIX packaging.
<br /><br />
To undo the Electron debug identity, use `winapp node clear-electron-debug-identity`.

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--manifest <path>` | Path to custom Package.appxmanifest (default: Package.appxmanifest in current directory) |
| `--no-install` | Do not install or modify dependencies; only configure the Electron debug identity |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` to the package name and application ID |
| `--verbose` | Enable verbose output |

**What it does:**

- Registers debug identity for electron.exe process
- Enables testing identity-requiring APIs in Electron development
- Uses existing Package.appxmanifest for identity configuration

**Examples:**

```bash
# Add identity to Electron development process
npx winapp node add-electron-debug-identity

# Use a custom manifest file
npx winapp node add-electron-debug-identity --manifest ./custom/Package.appxmanifest
```

---

### node clear-electron-debug-identity

*(Available in NPM package only)* Remove package identity from the Electron debug process by restoring the original electron.exe from backup.

```bash
npx winapp node clear-electron-debug-identity [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--verbose` | Enable verbose output |

**What it does:**

- Restores electron.exe from the backup created by `add-electron-debug-identity`
- Removes the backup files after restoration
- Returns Electron to its original state without package identity

**Examples:**

```bash
# Remove identity from Electron development process
npx winapp node clear-electron-debug-identity
```

---

### Global Options

All commands support these global options:

- `--verbose`, `-v` - Enable verbose output for detailed logging
- `--quiet`, `-q` - Suppress progress messages
- `--help`, `-h` - Show command help

---

### Global Cache Directory

Winapp creates a directory to cache files that can be shared between multiple projects.

By default, winapp creates a directory at `$UserProfile/.winapp` as the global cache directory.

To use a different location, set the `WINAPP_CLI_CACHE_DIRECTORY` environment variable.

In **cmd**:
```cmd
REM Set a custom location for winapp's global cache
set WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

In **PowerShell** and **pwsh**:
```pwsh
# Set a custom location for winapp's global cache
$env:WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

Winapp will create this directory automatically when you run commands like `init` or `restore`.

### Update Checks

The winapp CLI periodically checks for new versions and displays a one-line notice when an update is available. This check runs in the background and adds no latency to commands.

Update checks are automatically disabled in CI environments (GitHub Actions, Azure Pipelines, etc.).

To manually disable update checks, set the `WINAPP_CLI_UPDATE_CHECK` environment variable to `0`.

In **cmd**:
```cmd
set WINAPP_CLI_UPDATE_CHECK=0
```

In **PowerShell** and **pwsh**:
```pwsh
$env:WINAPP_CLI_UPDATE_CHECK = "0"
```

To make this permanent:
```powershell
[System.Environment]::SetEnvironmentVariable('WINAPP_CLI_UPDATE_CHECK', '0', 'User')
```

### ui

Inspect and interact with running Windows app UIs using UI Automation (UIA).

```bash
winapp ui [command] [options]
```

**Commands:**
- `status` - Connect to app and show info
- `inspect` - View element tree
- `search` - Find elements by selector
- `get-property` - Read element properties
- `get-text` / `get-value` - Read value/text from element (TextPattern, ValuePattern, or Name)
- `screenshot` - Capture window/element as PNG (auto-captures dialogs separately)
- `record` - Record a window/element region to an H.264 MP4 video (Windows Graphics Capture + Media Foundation)
- `invoke` - Activate element (click, toggle, expand)
- `click` - Click element via mouse simulation (for controls that don't support invoke)
- `hover` - Move mouse to element to trigger tooltips, flyouts, and hover states (default dwell: 800ms)
- `drag` - Drag the mouse from one point to another, by element selector or screen `x,y` coordinates (reorder, resize, sliders, drag-and-drop)
- `touch` - Inject synthetic touch gestures (tap, double-tap, long-press, swipe, pinch, stretch) at an element center or screen `x,y` coordinates
- `pen` - Inject synthetic pen/stylus input — taps and ink strokes with configurable pressure, tilt, and eraser mode
- `send-keys` - Send synthetic keyboard input (named keys, combos, raw vk=0xNN, or literal text) to a window
- `set-value` - Set value on editable element (text, number); falls back to LegacyIAccessible `put_accValue` for TextPattern-only rich-edit controls
- `focus` - Move keyboard focus
- `scroll-into-view` - Scroll element visible
- `wait-for` - Wait for element state
- `list-windows` - List all windows for an app
- `get-focused` - Report the currently focused element

**Options:**
- `-a, --app <app>` - Target app (name, title, or PID)
- `-w, --window <hwnd>` - Target window by HWND (stable)

#### ui record

Record the target window — or a single element's region — to an H.264 MP4 video. Frames are
captured via Windows Graphics Capture (with a PrintWindow fallback) and encoded incrementally with
Media Foundation, so long recordings never buffer in memory.

```bash
# Record a window for 10 seconds at 15 fps
winapp ui record -a Calculator --duration-sec 10 --fps 15 -o demo.mp4

# Record until Ctrl+C, downscaled so the longest edge is 1280px
winapp ui record -a "My App" --duration-sec 0 --max-edge 1280 -o capture.mp4

# Record just one element's region
winapp ui record -a "My App" btn-save-1234 -o button.mp4
```

**Record options:**
- `--duration-sec <n>` - Recording length in seconds. `0` records until Ctrl+C (default `0`).
- `--fps <n>` - Frames per second to capture (default `15`).
- `--max-edge <px>` - Downscale so the longest edge is at most this many pixels (`0` = no downscale).
- `--capture-screen` - Capture from the screen so overlays/popups are included (may capture occluding windows).
- `-o, --output <path>` - Output `.mp4` path (defaults to `recording-<timestamp>-<guid>.mp4`).

With `--json`, emits a `UiRecordResult` envelope including the output `path`, `frames`, `width`,
`height`, `fileSize`, `codec` (`"h264"`), and `mode` — the capture path actually used
(`wgc`, `printwindow`, or `screen`).

> **Known limitation:** recording a *specific element* inside a popup that renders in its own
> top-level window (WinUI/XAML flyout, teaching tip, tooltip) may capture the underlying main
> window instead. Record the whole window, or use `ui screenshot --capture-screen` for popup
> stills. Tracked in [#646](https://github.com/microsoft/winappCli/issues/646).

For full documentation, see [docs/ui-automation.md](ui-automation.md).
