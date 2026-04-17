# CLI Documentation and Usage

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

**What it does:**

- Creates `winapp.yaml` configuration file
- Downloads Windows SDK and Windows App SDK packages
- Generates C++/WinRT headers and binaries
- Creates AppxManifest.xml
- Sets up build tools and enables developer mode
- Updates .gitignore to exclude generated files
- Stores sharable files in the global cache directory

**Automatic .NET project detection:**

When a `.csproj` file is found in the target directory, `init` uses a streamlined .NET-specific flow:

- Validates and updates the `TargetFramework` to a Windows-compatible TFM (e.g., `net10.0-windows10.0.26100.0`)
- Adds `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` as NuGet `PackageReference` entries directly in the `.csproj`
- Generates `appxmanifest.xml`, assets, and a development certificate
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
winapp init --use-defaults --setup-sdks stable
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
- Stores sharable files in the global cache directory

> **Note:** For .NET projects initialized with `winapp init`, there is no `winapp.yaml`. Use `dotnet restore` to restore NuGet packages instead.

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

- `--config-dir <path>` - Directory containing winapp.yaml (default: current directory)
- `----setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)

**What it does:**

- Reads existing `winapp.yaml` configuration
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

Create MSIX packages from prepared application directories. Requires appxmanifest.xml file to be present in the target directory, in the current directory, or passed with the `--manifest` option. (run `init` or `manifest generate` to create a manifest)

```bash
winapp pack <input-folder> [options]
```

**Arguments:**

- `input-folder` - Directory containing the application files to package

**Options:**

- `--output <filename>` - Output MSIX file name (default: `<name>_<version>.msix`)
- `--name <name>` - Package name (default: from manifest)
- `--manifest <path>` - Path to AppxManifest.xml (default: auto-detect)
- `--cert <path>` - Path to signing certificate (enables auto-signing)
- `--cert-password <password>` - Certificate password (default: "password")
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--publisher <name>` - Publisher name for certificate generation
- `--self-contained` - Bundle Windows App SDK runtime
- `--skip-pri` - Skip PRI file generation
- `--executable <path>` - Path to the executable relative to the input folder (also `--exe`). Used to resolve `$targetnametoken$` placeholders in the manifest.

**What it does:**

- Validates and processes AppxManifest.xml files
- Resolves `$placeholder$` tokens in the manifest (see [Manifest placeholders](#manifest-placeholders) below)
- Ensures proper framework dependencies
- Updates side-by-side manifests with registrations
- Automatically discovers third-party WinRT components and registers their activatable classes (see [WinRT component discovery](#winrt-component-discovery) below)
- Handles self-contained WinAppSDK deployment
- Signs package if certificate provided

**WinRT component discovery:**

When packaging, `winapp pack` automatically scans NuGet packages defined in the `winapp.yaml` or `*.csproj` for third-party WinRT components (e.g., Win2D). It parses `.winmd` files to extract activatable class names and locates their implementation DLLs. The discovered entries are registered as follows:

- **Framework-dependent** (default): Activatable classes are added as `<InProcessServer>` entries in the `AppxManifest.xml`
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

- `--manifest <path>` - Path to AppxManifest.xml (default: `./appxmanifest.xml`)
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

Generate and manage AppxManifest.xml files.

#### manifest generate

Generate AppxManifest.xml from templates.

```bash
winapp manifest generate [directory] [options]
```

**Arguments:**

- `directory` - Directory to generate manifest in (default: current directory)

**Options:**

- `--package-name <name>` - Package name (default: folder name)
- `--publisher-name <name>` - Publisher CN (default: CN=\<current user\>)
- `--version <version>` - Version (default: "1.0.0.0")
- `--description <text>` - Description (default: "My Application")
- `--entrypoint <path>` - Entry point executable or script
- `--template <type>` - Template type: `packaged` (default) or `sparse`
- `--logo-path <path>` - Path to logo image file
- `--if-exists <Error|Overwrite|Skip>` - Set behavior if the certificate file already exists (default: Error)

**Templates:**

- `packaged` - Standard packaged app manifest
- `sparse` - App manifest using [sparse/external location packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)

**Manifest placeholders:**

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

- `--manifest <path>` - Path to AppxManifest.xml (default: auto-detect from input folder or current directory)
- `--output-appx-directory <path>` - Output directory for the loose layout package (default: `AppX` inside the input folder directory)
- `--args <string>` - Command-line arguments to pass to the application
- `--no-launch` - Only create the debug identity and register the package without launching the application
- `--with-alias` - Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a `uap5:ExecutionAlias` in the manifest (use `winapp manifest add-alias` to add one). Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
- `--debug-output` - Capture `OutputDebugString` messages and first-chance exceptions from the launched application. Framework noise (WinUI, COM, DirectX) is filtered from console output; the full log file captures everything. If the app crashes, automatically captures a minidump and analyzes it to show the exception type, message, and stack trace with source file:line numbers (resolved from PDBs in the build output folder). Managed (.NET) crashes are analyzed instantly with no external tools. Native (C++/WinRT) crashes show module names and offsets. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use `--no-launch` instead if you need to attach a different debugger. Cannot be combined with `--no-launch`. Cannot be combined with `--json`.
- `--symbols` - Download PDB symbols from Microsoft Symbol Server for richer native crash analysis with resolved function names. Only used with `--debug-output`. If omitted and a native crash occurs, the output will suggest adding this flag. First run downloads symbols and caches them locally; subsequent runs use the cache.
- `--unregister-on-exit` - Unregister the development package after the application exits. Only removes packages registered in development mode. Cannot be combined with `--no-launch`.
- `--detach` - Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with `--json`). Cannot be combined with `--no-launch`, `--debug-output`, `--with-alias`, or `--unregister-on-exit`.
- `--clean` - Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments.

**Application data persistence:**

By default, `winapp run` preserves your application's data (`LocalState`, `RoamingState`, `Settings`, etc.) when re-deploying. If your app writes data to `ApplicationData.Current.LocalFolder` or `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` within the package context, that data will survive across `winapp run` invocations.

Use `--clean` when you need a fresh start (e.g., to reset corrupted state or test first-run behavior).

**What it does:**

- Locates or generates the AppxManifest.xml
- Creates and registers a debug identity using a loose layout package
- Computes the Application User Model ID (AUMID)
- Launches the application using the registered identity (unless `--no-launch` is specified)
- Prints the process ID (PID) for debugger attachment

**Examples:**

```bash
# Register debug identity and launch app from build output
winapp run ./bin/Debug

# Launch with custom manifest and arguments
winapp run ./dist --manifest ./out/AppxManifest.xml --args "--my-flag value"

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

- `--manifest <path>` - Path to AppxManifest.xml (default: auto-detect from current directory)
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
winapp unregister --manifest ./appxmanifest.xml

# Force unregister even if registered from a different project tree
winapp unregister --force

# JSON output for scripting
winapp unregister --json
```

---

#### manifest add-alias

Add an execution alias (`uap5:AppExecutionAlias`) to an appxmanifest.xml. This allows launching the packaged app from the command line by typing the alias name.

```bash
winapp manifest add-alias [options]
```

**Options:**

- `--name <alias>` - Alias name (e.g. `myapp.exe`). Default: inferred from the `Executable` attribute in the manifest.
- `--manifest <path>` - Path to AppxManifest.xml (default: search current directory)
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
winapp manifest add-alias --manifest ./dist/appxmanifest.xml
```

Generate all required MSIX image assets from a single source image.

```bash
winapp manifest update-assets <image-path> [options]
```

**Arguments:**

- `image-path` - Path to source image file (PNG, JPG, SVG, ICO, GIF, BMP, etc.)

**Options:**

- `--manifest <path>` - Path to AppxManifest.xml file (default: search current directory)
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
winapp manifest update-assets mylogo.png --manifest ./dist/appxmanifest.xml

# Generate light theme variants from a separate image
winapp manifest update-assets mylogo.png --light-image mylogo-light.png

# Use the same image for both (generates all MRT light theme qualifiers)
winapp manifest update-assets mylogo.png --light-image mylogo.png

# With verbose output
winapp manifest update-assets mylogo.png --verbose
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

- `--manifest <appxmanifest.xml>` - Extract publisher information from appxmanifest.xml 
- `--publisher <name>` - Publisher name for certificate
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

Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: ([https://aka.ms/msstoredevcli](https://aka.ms/msstoredevcli)).

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

*(Available in NPM package only)* Add app identity to Electron development process by using sparse packaging. Requires an appxmanifest.xml (create one with `winapp init` or `winapp manifest generate` if you don't have one).

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
| `--manifest <path>` | Path to custom appxmanifest.xml (default: appxmanifest.xml in current directory) |
| `--no-install` | Do not install or modify dependencies; only configure the Electron debug identity |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` to the package name and application ID |
| `--verbose` | Enable verbose output |

**What it does:**

- Registers debug identity for electron.exe process
- Enables testing identity-requiring APIs in Electron development
- Uses existing AppxManifest.xml for identity configuration

**Examples:**

```bash
# Add identity to Electron development process
npx winapp node add-electron-debug-identity

# Use a custom manifest file
npx winapp node add-electron-debug-identity --manifest ./custom/appxmanifest.xml
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

In **Powershell** and **pwsh**:
```pwsh
# Set a custom location for winapp's global cache
$env:WINAPP_CLI_CACHE_DIRECTORY=d:\temp\.winapp
```

Winapp will create this directory automatically when you run commands like `init` or `restore`.
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
- `invoke` - Activate element (click, toggle, expand)
- `click` - Click element via mouse simulation (for controls that don't support invoke)
- `set-value` - Set value on editable element (text, number)
- `focus` - Move keyboard focus
- `scroll-into-view` - Scroll element visible
- `wait-for` - Wait for element state
- `list-windows` - List all windows for an app
- `get-focused` - Report the currently focused element

**Options:**
- `-a, --app <app>` - Target app (name, title, or PID)
- `-w, --window <hwnd>` - Target window by HWND (stable)

For full documentation, see [docs/ui-automation.md](ui-automation.md).
