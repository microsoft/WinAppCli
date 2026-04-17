<!-- AUTO-GENERATED — DO NOT EDIT -->
<!-- Regenerate with: cd src/winapp-npm && npm run generate-docs -->

# NPM Package — Programmatic API

TypeScript/JavaScript API reference for `@microsoft/winappcli`.
Each CLI command is available as an async function that captures stdout/stderr and returns a typed result.
Helper utilities for MSIX identity, Electron debug identity, and build tools are also exported.

## Installation

```bash
npm install @microsoft/winappcli
```

## Quick start

```typescript
import { init, packageApp, certGenerate } from '@microsoft/winappcli';

// Initialize a new project with defaults
await init({ useDefaults: true });

// Generate a dev certificate
await certGenerate({ install: true });

// Package the built app
await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });
```

## Common types

Every CLI command wrapper accepts an options object extending `CommonOptions` and returns `Promise<WinappResult>`.

### `CommonOptions`

Base options shared by most commands.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `WinappResult`

Result returned by every command wrapper.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes | Process exit code (always 0 on success – non-zero throws). |
| `stdout` | `string` | Yes | Captured standard output. |
| `stderr` | `string` | Yes | Captured standard error. |

## CLI command wrappers

These functions wrap native `winapp` CLI commands. All accept [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).

### `certGenerate()`

Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.

```typescript
function certGenerate(options?: CertGenerateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exportCer` | `boolean \| undefined` | No | Export a .cer file (public key only) alongside the .pfx |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `install` | `boolean \| undefined` | No | Install the certificate to the local machine store after generation |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from |
| `output` | `string \| undefined` | No | Output path for the generated PFX file |
| `password` | `string \| undefined` | No | Password for the generated PFX file |
| `publisher` | `string \| undefined` | No | Publisher name for the generated certificate. If not specified, will be inferred from manifest. |
| `validDays` | `number \| undefined` | No | Number of days the certificate is valid |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `certInfo()`

Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.

```typescript
function certInfo(options: CertInfoOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `password` | `string \| undefined` | No | Password for the PFX file |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `certInstall()`

Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.

```typescript
function certInstall(options: CertInstallOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX or CER) |
| `force` | `boolean \| undefined` | No | Force installation even if the certificate already exists |
| `password` | `string \| undefined` | No | Password for the PFX file |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `createDebugIdentity()`

Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires Package.appxmanifest in current directory or passed via --manifest. Re-run after changing the manifest or Assets/.

```typescript
function createDebugIdentity(options?: CreateDebugIdentityOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `entrypoint` | `string \| undefined` | No | Path to the .exe that will need to run with identity, or entrypoint script. |
| `keepIdentity` | `boolean \| undefined` | No | Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest or appxmanifest.xml |
| `noInstall` | `boolean \| undefined` | No | Do not install the package after creation. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `createExternalCatalog()`

Generates a CodeIntegrityExternal.cat catalog file with hashes of executable files from specified directories. Used with the TrustedLaunch flag in MSIX sparse package manifests (AllowExternalContent) to allow execution of external files not included in the package.

```typescript
function createExternalCatalog(options: CreateExternalCatalogOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | List of input folders with executable files to process (separated by semicolons) |
| `computeFlatHashes` | `boolean \| undefined` | No | Include flat hashes when generating the catalog |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file already exists |
| `output` | `string \| undefined` | No | Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. |
| `recursive` | `boolean \| undefined` | No | Include files from subdirectories |
| `usePageHashes` | `boolean \| undefined` | No | Include page hashes when generating the catalog |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `getWinappPath()`

Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.

```typescript
function getWinappPath(options?: GetWinappPathOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `global` | `boolean \| undefined` | No | Get the global .winapp directory instead of local |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `init()`

Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.

```typescript
function init(options?: InitOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace, for consumption or installation. |
| `configDir` | `string \| undefined` | No | Directory to read/store configuration (default: current directory) |
| `configOnly` | `boolean \| undefined` | No | Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. |
| `ignoreConfig` | `boolean \| undefined` | No | Don't use configuration file for version management |
| `noGitignore` | `boolean \| undefined` | No | Don't update .gitignore file |
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `useDefaults` | `boolean \| undefined` | No | Do not prompt, and use default of all prompts |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestAddAlias()`

Add an execution alias (uap5:AppExecutionAlias) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name. By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).

```typescript
function manifestAddAlias(options?: ManifestAddAliasOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `appId` | `string \| undefined` | No | Application Id to add the alias to (default: first Application element) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `name` | `string \| undefined` | No | Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestGenerate()`

Create Package.appxmanifest without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs).

```typescript
function manifestGenerate(options?: ManifestGenerateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `directory` | `string \| undefined` | No | Directory to generate manifest in |
| `description` | `string \| undefined` | No | Human-readable app description shown during installation and in Windows Settings |
| `executable` | `string \| undefined` | No | Path to the application's executable. Default: <package-name>.exe |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `logoPath` | `string \| undefined` | No | Path to logo image file |
| `packageName` | `string \| undefined` | No | Package name (default: folder name) |
| `publisherName` | `string \| undefined` | No | Publisher CN (default: CN=<current user>) |
| `template` | `ManifestTemplates \| undefined` | No | Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) |
| `version` | `string \| undefined` | No | App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `manifestUpdateAssets()`

Generate new assets for images referenced in a Package.appxmanifest from a single source image. Source image should be at least 400x400 pixels.

```typescript
function manifestUpdateAssets(options: ManifestUpdateAssetsOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `imagePath` | `string` | Yes | Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) |
| `lightImage` | `string \| undefined` | No | Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `packageApp()`

Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx

```typescript
function packageApp(options: PackageOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | Input folder with package layout |
| `cert` | `string \| undefined` | No | Path to signing certificate (will auto-sign if provided) |
| `certPassword` | `string \| undefined` | No | Certificate password (default: password) |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. |
| `generateCert` | `boolean \| undefined` | No | Generate a new development certificate |
| `installCert` | `boolean \| undefined` | No | Install certificate to machine |
| `manifest` | `string \| undefined` | No | Path to AppX manifest file (default: auto-detect from input folder or current directory) |
| `name` | `string \| undefined` | No | Package name (default: from manifest) |
| `output` | `string \| undefined` | No | Output msix file name for the generated package (defaults to <name>.msix) |
| `publisher` | `string \| undefined` | No | Publisher name for certificate generation |
| `selfContained` | `boolean \| undefined` | No | Bundle Windows App SDK runtime for self-contained deployment |
| `skipPri` | `boolean \| undefined` | No | Skip PRI file generation |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `restore()`

Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages from existing winapp.yaml without changing versions. Requires winapp.yaml (created by 'init'). To check for newer SDK versions, use 'update' instead.

```typescript
function restore(options?: RestoreOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace |
| `configDir` | `string \| undefined` | No | Directory to read configuration from (default: current directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `run()`

Creates packaged layout, registers the Application, and launches the packaged application.

```typescript
function run(options: RunOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | Input folder containing the app to run |
| `args` | `string \| undefined` | No | Command-line arguments to pass to the application |
| `clean` | `boolean \| undefined` | No | Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. |
| `debugOutput` | `boolean \| undefined` | No | Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. Cannot be combined with --no-launch or --json. |
| `detach` | `boolean \| undefined` | No | Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) |
| `noLaunch` | `boolean \| undefined` | No | Only create the debug identity and register the package without launching the application |
| `outputAppxDirectory` | `string \| undefined` | No | Output directory for the loose layout package. If not specified, a directory named AppX inside the input-folder directory will be used. |
| `symbols` | `boolean \| undefined` | No | Download symbols from Microsoft Symbol Server for richer native crash analysis. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. |
| `unregisterOnExit` | `boolean \| undefined` | No | Unregister the development package after the application exits. Only removes packages registered in development mode. |
| `withAlias` | `boolean \| undefined` | No | Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `sign()`

Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.

```typescript
function sign(options: SignOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file/package to sign |
| `certPath` | `string` | Yes | Path to the certificate file (PFX format) |
| `password` | `string \| undefined` | No | Certificate password |
| `timestamp` | `string \| undefined` | No | Timestamp server URL |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `store()`

Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: https://aka.ms/msstoredevcli

```typescript
function store(options?: StoreOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `storeArgs` | `string[] \| undefined` | No | Arguments to pass through to the Microsoft Store Developer CLI. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `tool()`

Run Windows SDK tools directly (makeappx, signtool, makepri, etc.). Auto-downloads Build Tools if needed. For most tasks, prefer higher-level commands like 'package' or 'sign'. Example: winapp tool makeappx pack /d ./folder /p ./out.msix

```typescript
function tool(options?: ToolOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `toolArgs` | `string[] \| undefined` | No | Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiClick()`

Click an element by slug or text search using mouse simulation. Works on elements that don't support InvokePattern (e.g., column headers, list items). Use --double for double-click, --right for right-click.

```typescript
function uiClick(options?: UiClickOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `double` | `boolean \| undefined` | No | Perform a double-click instead of a single click |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Perform a right-click instead of a left click |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiFocus()`

Move keyboard focus to the specified element using UIA SetFocus.

```typescript
function uiFocus(options?: UiFocusOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetFocused()`

Show the element that currently has keyboard focus in the target app.

```typescript
function uiGetFocused(options?: UiGetFocusedOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetProperty()`

Read UIA property values from an element. Specify --property for a single property or omit for all.

```typescript
function uiGetProperty(options?: UiGetPropertyOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiGetValue()`

Read the current value from an element. Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). Usage: winapp ui get-value <selector> -a <app>

```typescript
function uiGetValue(options?: UiGetValueOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiInspect()`

View the UI element tree with semantic slugs, element types, names, and bounds.

```typescript
function uiInspect(options?: UiInspectOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `ancestors` | `boolean \| undefined` | No | Walk up the tree from the specified element to the root |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `depth` | `number \| undefined` | No | Tree inspection depth |
| `hideDisabled` | `boolean \| undefined` | No | Hide disabled elements from output |
| `hideOffscreen` | `boolean \| undefined` | No | Hide offscreen elements from output |
| `interactive` | `boolean \| undefined` | No | Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiInvoke()`

Activate an element by slug or text search. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.

```typescript
function uiInvoke(options?: UiInvokeOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiListWindows()`

List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.

```typescript
function uiListWindows(options?: UiListWindowsOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScreenshot()`

Capture the target window or element as a PNG image. When multiple windows exist (e.g., dialogs), captures each to a separate file. With --json, returns file path and dimensions. Use --capture-screen for popup overlays.

```typescript
function uiScreenshot(options?: UiScreenshotOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `captureScreen` | `boolean \| undefined` | No | Capture from screen (includes popups/overlays) instead of window rendering. Brings window to foreground first. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `output` | `string \| undefined` | No | Save output to file path (e.g., screenshot) |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScroll()`

Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, or --to to jump to top/bottom.

```typescript
function uiScroll(options?: UiScrollOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `direction` | `string \| undefined` | No | Scroll direction: up, down, left, right |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `to` | `string \| undefined` | No | Scroll to position: top, bottom |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiScrollIntoView()`

Scroll the specified element into the visible area using UIA ScrollItemPattern.

```typescript
function uiScrollIntoView(options?: UiScrollIntoViewOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiSearch()`

Search the element tree for elements matching a text query. Returns all matches with semantic slugs.

```typescript
function uiSearch(options?: UiSearchOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `max` | `number \| undefined` | No | Maximum search results |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiSetValue()`

Set a value on an element using UIA ValuePattern. Works for TextBox, ComboBox, Slider, and other editable controls. Usage: winapp ui set-value <selector> <value> -a <app>

```typescript
function uiSetValue(options?: UiSetValueOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `value` | `string \| undefined` | No | Value to set (text for TextBox/ComboBox, number for Slider) |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiStatus()`

Connect to a target app and display connection info.

```typescript
function uiStatus(options?: UiStatusOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `uiWaitFor()`

Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.

```typescript
function uiWaitFor(options?: UiWaitForOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `contains` | `boolean \| undefined` | No | Use substring matching for --value instead of exact match |
| `gone` | `boolean \| undefined` | No | Wait for element to disappear instead of appear |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `timeout` | `number \| undefined` | No | Timeout in milliseconds |
| `value` | `string \| undefined` | No | Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `unregister()`

Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').

```typescript
function unregister(options?: UnregisterOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Skip the install-location directory check and unregister even if the package was registered from a different project tree |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from current directory) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

### `update()`

Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.

```typescript
function update(options?: UpdateOptions): Promise<WinappResult>
```

**Options:**

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |

*Also accepts [CommonOptions](#commonoptions) (`quiet`, `verbose`, `cwd`).*

---

## Utility functions

### `execWithBuildTools()`

Execute a command with BuildTools bin path added to PATH environment

```typescript
function execWithBuildTools(command: string, options?: ExecSyncOptions): string | Buffer<ArrayBufferLike>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | `string` | Yes | The command to execute |
| `options` | `ExecSyncOptions` | No | Options to pass to execSync (optional) |

**Returns:** The output from execSync

---

### `addMsixIdentityToExe()`

Adds package identity information from an appxmanifest.xml file to an executable's embedded manifest

```typescript
function addMsixIdentityToExe(exePath: string, appxManifestPath?: string | undefined, options?: MsixIdentityOptions): Promise<MsixIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `exePath` | `string` | Yes | Path to the executable file |
| `appxManifestPath` | `string \| undefined` | No | Path to the appxmanifest.xml file containing package identity data |
| `options` | `MsixIdentityOptions` | No | Optional configuration |

---

### `addElectronDebugIdentity()`

Adds package identity to the Electron debug process

```typescript
function addElectronDebugIdentity(options?: MsixIdentityOptions): Promise<ElectronDebugIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `MsixIdentityOptions` | No | Configuration options |

---

### `clearElectronDebugIdentity()`

Clears/removes package identity from the Electron debug process by restoring from backup

```typescript
function clearElectronDebugIdentity(options?: MsixIdentityOptions): Promise<ClearElectronDebugIdentityResult>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `options` | `MsixIdentityOptions` | No | Configuration options |

---

### `getGlobalWinappPath()`

Get the path to the global .winapp directory

```typescript
function getGlobalWinappPath(): string
```

**Returns:** The full path to the global .winapp directory

---

### `getLocalWinappPath()`

Get the path to the local .winapp directory

```typescript
function getLocalWinappPath(): string
```

**Returns:** The full path to the local .winapp directory

---

## Node.js CLI commands

These commands are available exclusively via `npx winapp node <subcommand>` and are not exported as programmatic functions.

### `node create-addon`

Generate native addon files for an Electron project.  Supports C++ (node-gyp) and C# (node-api-dotnet) templates.

```bash
npx winapp node create-addon [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--name <name>` | Addon name (default depends on template) |
| `--template <type>` | Addon template: `cpp` or `cs` (default: `cpp`) |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `package.json`).

**Examples:**

```bash
npx winapp node create-addon
npx winapp node create-addon --name myAddon
npx winapp node create-addon --template cs --name MyCsAddon
```

---

### `node add-electron-debug-identity`

Add package identity to the Electron debug process using sparse packaging.  Creates a backup of `electron.exe`, generates a sparse MSIX manifest, adds identity to the executable, and registers the sparse package.  Requires an `appxmanifest.xml` (create one with `winapp init` or `winapp manifest generate`).

```bash
npx winapp node add-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--manifest <path>` | Path to custom `appxmanifest.xml` (default: `appxmanifest.xml` in current directory) |
| `--no-install` | Do not install the package after creation |
| `--keep-identity` | Keep the manifest identity as-is, without appending `.debug` suffix |
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).  To undo, use `npx winapp node clear-electron-debug-identity`.

**Examples:**

```bash
npx winapp node add-electron-debug-identity
npx winapp node add-electron-debug-identity --manifest ./custom/appxmanifest.xml
```

---

### `node clear-electron-debug-identity`

Remove package identity from the Electron debug process.  Restores `electron.exe` from the backup created by `add-electron-debug-identity` and removes the backup files.

```bash
npx winapp node clear-electron-debug-identity [options]
```

**Options:**

| Flag | Description |
|------|-------------|
| `--verbose` | Enable verbose output |

> **Note:** Must be run from the root of an Electron project (directory containing `node_modules/electron`).

**Examples:**

```bash
npx winapp node clear-electron-debug-identity
```

---

## Types reference

### `ExecSyncOptions`

Re-exported from Node.js for convenience. See [Node.js docs](https://nodejs.org/api/child_process.html).

### `MsixIdentityOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `verbose` | `boolean \| undefined` | No |  |
| `noInstall` | `boolean \| undefined` | No |  |
| `keepIdentity` | `boolean \| undefined` | No |  |
| `manifest` | `string \| undefined` | No |  |

### `MsixIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |

### `ElectronDebugIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `electronExePath` | `string` | Yes |  |
| `backupPath` | `string` | Yes |  |
| `manifestPath` | `string` | Yes |  |
| `assetsDir` | `string` | Yes |  |

### `ClearElectronDebugIdentityResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `electronExePath` | `string` | Yes |  |
| `restoredFromBackup` | `boolean` | Yes |  |

### `CallWinappCliOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitOnError` | `boolean \| undefined` | No |  |

### `CallWinappCliResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes |  |

### `CallWinappCliCaptureOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()) |

### `CallWinappCliCaptureResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exitCode` | `number` | Yes |  |
| `stdout` | `string` | Yes |  |
| `stderr` | `string` | Yes |  |

### `GenerateCppAddonOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | `string \| undefined` | No |  |
| `projectRoot` | `string \| undefined` | No |  |
| `verbose` | `boolean \| undefined` | No |  |

### `GenerateCppAddonResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `addonName` | `string` | Yes |  |
| `addonPath` | `string` | Yes |  |
| `needsTerminalRestart` | `boolean` | Yes |  |
| `files` | `string[]` | Yes |  |

### `GenerateCsAddonOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | `string \| undefined` | No |  |
| `projectRoot` | `string \| undefined` | No |  |
| `verbose` | `boolean \| undefined` | No |  |

### `GenerateCsAddonResult`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `success` | `boolean` | Yes |  |
| `addonName` | `string` | Yes |  |
| `addonPath` | `string` | Yes |  |
| `needsTerminalRestart` | `boolean` | Yes |  |
| `files` | `string[]` | Yes |  |

### `IfExists`

IfExists values.

```typescript
type IfExists = "error" | "overwrite" | "skip"
```

### `SdkInstallMode`

SdkInstallMode values.

```typescript
type SdkInstallMode = "stable" | "preview" | "experimental" | "none"
```

### `ManifestTemplates`

ManifestTemplates values.

```typescript
type ManifestTemplates = "packaged" | "sparse"
```

### `CertGenerateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `exportCer` | `boolean \| undefined` | No | Export a .cer file (public key only) alongside the .pfx |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `install` | `boolean \| undefined` | No | Install the certificate to the local machine store after generation |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from |
| `output` | `string \| undefined` | No | Output path for the generated PFX file |
| `password` | `string \| undefined` | No | Password for the generated PFX file |
| `publisher` | `string \| undefined` | No | Publisher name for the generated certificate. If not specified, will be inferred from manifest. |
| `validDays` | `number \| undefined` | No | Number of days the certificate is valid |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CertInfoOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX) |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `password` | `string \| undefined` | No | Password for the PFX file |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CertInstallOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `certPath` | `string` | Yes | Path to the certificate file (PFX or CER) |
| `force` | `boolean \| undefined` | No | Force installation even if the certificate already exists |
| `password` | `string \| undefined` | No | Password for the PFX file |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CreateDebugIdentityOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `entrypoint` | `string \| undefined` | No | Path to the .exe that will need to run with identity, or entrypoint script. |
| `keepIdentity` | `boolean \| undefined` | No | Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest or appxmanifest.xml |
| `noInstall` | `boolean \| undefined` | No | Do not install the package after creation. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `CreateExternalCatalogOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | List of input folders with executable files to process (separated by semicolons) |
| `computeFlatHashes` | `boolean \| undefined` | No | Include flat hashes when generating the catalog |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file already exists |
| `output` | `string \| undefined` | No | Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. |
| `recursive` | `boolean \| undefined` | No | Include files from subdirectories |
| `usePageHashes` | `boolean \| undefined` | No | Include page hashes when generating the catalog |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `GetWinappPathOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `global` | `boolean \| undefined` | No | Get the global .winapp directory instead of local |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `InitOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace, for consumption or installation. |
| `configDir` | `string \| undefined` | No | Directory to read/store configuration (default: current directory) |
| `configOnly` | `boolean \| undefined` | No | Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. |
| `ignoreConfig` | `boolean \| undefined` | No | Don't use configuration file for version management |
| `noGitignore` | `boolean \| undefined` | No | Don't update .gitignore file |
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `useDefaults` | `boolean \| undefined` | No | Do not prompt, and use default of all prompts |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestAddAliasOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `appId` | `string \| undefined` | No | Application Id to add the alias to (default: first Application element) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `name` | `string \| undefined` | No | Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestGenerateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `directory` | `string \| undefined` | No | Directory to generate manifest in |
| `description` | `string \| undefined` | No | Human-readable app description shown during installation and in Windows Settings |
| `executable` | `string \| undefined` | No | Path to the application's executable. Default: <package-name>.exe |
| `ifExists` | `IfExists \| undefined` | No | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) |
| `logoPath` | `string \| undefined` | No | Path to logo image file |
| `packageName` | `string \| undefined` | No | Package name (default: folder name) |
| `publisherName` | `string \| undefined` | No | Publisher CN (default: CN=<current user>) |
| `template` | `ManifestTemplates \| undefined` | No | Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) |
| `version` | `string \| undefined` | No | App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ManifestUpdateAssetsOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `imagePath` | `string` | Yes | Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) |
| `lightImage` | `string \| undefined` | No | Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) |
| `manifest` | `string \| undefined` | No | Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `PackageOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | Input folder with package layout |
| `cert` | `string \| undefined` | No | Path to signing certificate (will auto-sign if provided) |
| `certPassword` | `string \| undefined` | No | Certificate password (default: password) |
| `executable` | `string \| undefined` | No | Path to the executable relative to the input folder. |
| `generateCert` | `boolean \| undefined` | No | Generate a new development certificate |
| `installCert` | `boolean \| undefined` | No | Install certificate to machine |
| `manifest` | `string \| undefined` | No | Path to AppX manifest file (default: auto-detect from input folder or current directory) |
| `name` | `string \| undefined` | No | Package name (default: from manifest) |
| `output` | `string \| undefined` | No | Output msix file name for the generated package (defaults to <name>.msix) |
| `publisher` | `string \| undefined` | No | Publisher name for certificate generation |
| `selfContained` | `boolean \| undefined` | No | Bundle Windows App SDK runtime for self-contained deployment |
| `skipPri` | `boolean \| undefined` | No | Skip PRI file generation |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `RestoreOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `baseDirectory` | `string \| undefined` | No | Base/root directory for the winapp workspace |
| `configDir` | `string \| undefined` | No | Directory to read configuration from (default: current directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `RunOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `inputFolder` | `string` | Yes | Input folder containing the app to run |
| `args` | `string \| undefined` | No | Command-line arguments to pass to the application |
| `clean` | `boolean \| undefined` | No | Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. |
| `debugOutput` | `boolean \| undefined` | No | Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. Cannot be combined with --no-launch or --json. |
| `detach` | `boolean \| undefined` | No | Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) |
| `noLaunch` | `boolean \| undefined` | No | Only create the debug identity and register the package without launching the application |
| `outputAppxDirectory` | `string \| undefined` | No | Output directory for the loose layout package. If not specified, a directory named AppX inside the input-folder directory will be used. |
| `symbols` | `boolean \| undefined` | No | Download symbols from Microsoft Symbol Server for richer native crash analysis. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. |
| `unregisterOnExit` | `boolean \| undefined` | No | Unregister the development package after the application exits. Only removes packages registered in development mode. |
| `withAlias` | `boolean \| undefined` | No | Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `SignOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `filePath` | `string` | Yes | Path to the file/package to sign |
| `certPath` | `string` | Yes | Path to the certificate file (PFX format) |
| `password` | `string \| undefined` | No | Certificate password |
| `timestamp` | `string \| undefined` | No | Timestamp server URL |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `StoreOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `storeArgs` | `string[] \| undefined` | No | Arguments to pass through to the Microsoft Store Developer CLI. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `ToolOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `toolArgs` | `string[] \| undefined` | No | Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiClickOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `double` | `boolean \| undefined` | No | Perform a double-click instead of a single click |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `right` | `boolean \| undefined` | No | Perform a right-click instead of a left click |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiFocusOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetFocusedOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetPropertyOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiGetValueOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiInspectOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `ancestors` | `boolean \| undefined` | No | Walk up the tree from the specified element to the root |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `depth` | `number \| undefined` | No | Tree inspection depth |
| `hideDisabled` | `boolean \| undefined` | No | Hide disabled elements from output |
| `hideOffscreen` | `boolean \| undefined` | No | Hide offscreen elements from output |
| `interactive` | `boolean \| undefined` | No | Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiInvokeOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiListWindowsOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScreenshotOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `captureScreen` | `boolean \| undefined` | No | Capture from screen (includes popups/overlays) instead of window rendering. Brings window to foreground first. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `output` | `string \| undefined` | No | Save output to file path (e.g., screenshot) |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScrollOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `direction` | `string \| undefined` | No | Scroll direction: up, down, left, right |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `to` | `string \| undefined` | No | Scroll to position: top, bottom |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiScrollIntoViewOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiSearchOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `max` | `number \| undefined` | No | Maximum search results |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiSetValueOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `value` | `string \| undefined` | No | Value to set (text for TextBox/ComboBox, number for Slider) |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiStatusOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UiWaitForOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `selector` | `string \| undefined` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `app` | `string \| undefined` | No | Target app (process name, window title, or PID). Lists windows if ambiguous. |
| `contains` | `boolean \| undefined` | No | Use substring matching for --value instead of exact match |
| `gone` | `boolean \| undefined` | No | Wait for element to disappear instead of appear |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `property` | `string \| undefined` | No | Property name to read or filter on |
| `timeout` | `number \| undefined` | No | Timeout in milliseconds |
| `value` | `string \| undefined` | No | Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. |
| `window` | `number \| undefined` | No | Target window by HWND (stable handle from list output). Takes precedence over --app. |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UnregisterOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `force` | `boolean \| undefined` | No | Skip the install-location directory check and unregister even if the package was registered from a different project tree |
| `json` | `boolean \| undefined` | No | Format output as JSON |
| `manifest` | `string \| undefined` | No | Path to the Package.appxmanifest (default: auto-detect from current directory) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

### `UpdateOptions`

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `setupSdks` | `SdkInstallMode \| undefined` | No | SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) |
| `quiet` | `boolean \| undefined` | No | Suppress progress messages. |
| `verbose` | `boolean \| undefined` | No | Enable verbose output. |
| `cwd` | `string \| undefined` | No | Working directory for the CLI process (defaults to process.cwd()). |

