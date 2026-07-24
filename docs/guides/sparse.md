<!-- mslearn: true -->
# Sparse packaging: grant identity to an unpackaged app

> For a working end-to-end example (WPF app + Inno Setup installer), see the [sparse-app](../../samples/sparse-app) sample.

A standard desktop executable — built with `dotnet build`, MSBuild, CMake, or any other toolchain — has no [package identity](https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview). Without identity, it cannot use many modern Windows APIs (toast notifications, background tasks, share targets, startup tasks, the app data APIs, and more).

**Sparse packaging** grants identity to an app *without* moving its binaries into an MSIX. You ship a tiny **identity-only** `.msix` (just a manifest) and register it alongside your normally-installed app using an *external location*. Your `.exe` stays exactly where your installer puts it. This is the production counterpart to [`winapp create-debug-identity`](../usage.md#create-debug-identity), which is for developer-time debugging only.

This guide covers the three CLI steps that map to the first three steps of the official [Grant identity to non-packaged apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) workflow:

| Step | Command | Result |
|------|---------|--------|
| 1. Create the identity manifest | `winapp init --exe <exe> --sparse` | `appxmanifest.xml` + `Assets/` |
| 2. Build & sign the identity package | `winapp pack <appxmanifest.xml> --cert <pfx>` | `<PackageName>.identity.msix` |
| 3. Embed identity into the app | `winapp embed-identity <exe>` | `<msix>` element in the exe's fusion manifest |

Steps 4–5 of the docs (register / unregister the package) are your **installer's** responsibility — see [Installer integration](#installer-integration).

## When to use sparse packaging

- You already have a mature installer (Inno Setup, WiX, NSIS, MSI) and don't want to switch to MSIX for distribution, but you need identity-gated Windows APIs.
- Your app must install to a path or with a layout that MSIX doesn't allow.
- You want a minimal, additive change: keep your existing install flow and add one `.msix` registration step.

If you're starting fresh and can distribute as MSIX, a full packaged app (`winapp init` + `winapp pack <folder>`) is simpler.

## Prerequisites

1. **Windows 10, version 2004 (build 19041) or later.** Sparse packages rely on `uap10:AllowExternalContent`, which requires 19041+.
2. **winapp CLI** — install via winget (or update if already installed):
   ```powershell
   winget install Microsoft.WinApp --source winget
   ```
3. **A code-signing certificate** trusted on the target machine. For local testing, generate a development certificate with [`winapp cert generate`](../usage.md#cert) and trust it. Production packages must be signed with a certificate whose subject matches the manifest `Publisher`.

## Walkthrough

The examples below assume a built executable at `./bin/Release/net8.0-windows/MyApp.exe`.

### Step 1 — Create the sparse identity manifest

```powershell
winapp init --exe ./bin/Release/net8.0-windows/MyApp.exe --sparse
```

This infers the package name, publisher, version, and description from the exe (via its file version info) and prompts you to accept or override them. Add `--use-defaults` (or `--no-prompt`) to skip the prompts in CI, and `--name` / `--publisher` to override specific values:

```powershell
winapp init --exe ./bin/Release/net8.0-windows/MyApp.exe --sparse --use-defaults `
  --name "Contoso.MyApp" --publisher "CN=Contoso"
```

It writes, next to the exe (or to `--output-dir`):

- `appxmanifest.xml` — a sparse manifest with `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>` (an element under `<Properties>`), `ProcessorArchitecture="neutral"`, a `win32App` application, and the exe name filled into `Executable`.
- `Assets/` — placeholder visual assets (extracted from the exe's icon when possible).

> **Note:** The sparse init flow deliberately **skips all SDK/package installation** — identity-only packages have no SDK dependencies.

If an `appxmanifest.xml` already exists in the target directory, init stops rather than overwriting it (and its `Assets/`). Re-run with `--force` to regenerate it.

Make sure the `Publisher` in the generated manifest matches the certificate you'll sign with. Edit `appxmanifest.xml` if needed, or pass `--publisher` when generating.

### Step 2 — Build and sign the identity package

Point `winapp pack` at the sparse manifest (a file, not a folder):

```powershell
winapp pack ./bin/Release/net8.0-windows/appxmanifest.xml --cert ./devcert.pfx
```

Because the manifest declares `AllowExternalContent`, `winapp pack` builds an **identity-only** `.msix` containing just the manifest — no binaries, no assets. The output defaults to `<PackageName>.identity.msix` in the current directory; use `--output` to change it. Signing happens only when you pass `--cert` (or `--generate-cert`).

### Step 3 — Embed identity into your app

Embed the `<msix>` element so Windows connects the running exe to the identity package:

```powershell
# EXE mode — modify the built binary in place (uses mt.exe)
winapp embed-identity ./bin/Release/net8.0-windows/MyApp.exe
```

Or maintain the side-by-side manifest as a checked-in file and rebuild:

```powershell
# XML mode — update an external SxS manifest, then rebuild your app
winapp embed-identity ./app.manifest
```

In XML mode the `<msix>` element is inserted into (or replaced in) the target manifest. Reference that manifest from your project (for .NET, set `<ApplicationManifest>app.manifest</ApplicationManifest>`) and rebuild so the element is embedded in the exe.

Both modes read identity from a sparse `appxmanifest.xml`. When you omit `--manifest`, winapp looks next to the target first (where `winapp init --exe --sparse` writes it), then falls back to the current directory; pass `--manifest` to point elsewhere.

> **Note:** EXE mode rewrites the binary with `mt.exe`, which invalidates any existing Authenticode signature. Re-sign the exe (e.g. `winapp sign ./MyApp.exe <cert.pfx>`) before distributing it.

### Step 4 — Register (for local testing)

Register the identity package against the folder that contains your exe (the *external location*):

```powershell
Add-AppxPackage -Path .\MyApp.identity.msix `
  -ExternalLocation (Resolve-Path .\bin\Release\net8.0-windows)
```

Launch the app and confirm identity is present — for example, `Windows.ApplicationModel.Package.Current.Id.FamilyName` should return your package family name instead of throwing.

To clean up:

```powershell
Remove-AppxPackage <full-package-name>
```

## Asset handling

The sparse `.msix` is **identity-only**. The visual assets referenced by the manifest (`Assets\StoreLogo.png`, tiles, etc.) are resolved from the **external content location** at runtime — i.e., from your app's install directory — **not** from inside the `.msix`.

This means you must **deploy the `Assets/` folder alongside your application** (same layout the manifest expects, relative to the external location). If you pack a *folder* that contains assets or binaries, `winapp pack` will warn you: for sparse packages those files belong at the external location, not in the package.

## Installer integration

Registration and unregistration are the installer's job. The pattern is the same across installer tools:

- **Install:** copy your app binaries, the `Assets/` folder, and the `.msix` to the install directory, then run
  `Add-AppxPackage -Path "<install-dir>\MyApp.identity.msix" -ExternalLocation "<install-dir>"`.
- **Uninstall:** run `Remove-AppxPackage <full-package-name>` before deleting files.

> **Security:** the install directory is resolved at install time and may contain characters
> (e.g. a single quote) that break out of a PowerShell string literal. Always escape or validate
> the path before interpolating it into a `-Command` string — the WiX and NSIS snippets below
> assume a trusted install path, while the Inno Setup example demonstrates safe escaping. Prefer
> passing paths as arguments to a `-File` script over inline `-Command` interpolation.

### Inno Setup

Build the PowerShell arguments in a `[Code]` function so the runtime install path is escaped
for the single-quoted PowerShell literal (an install directory containing a `'` must not be able
to inject script):

```pascal
[Files]
Source: "dist\*"; DestDir: "{app}"; Flags: recursesubdirs
Source: "MyApp.identity.msix"; DestDir: "{app}"

[Run]
Filename: "powershell.exe"; Parameters: "{code:RegisterParams}"; Flags: runhidden

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'MyApp' | Remove-AppxPackage"""; \
  Flags: runhidden

[Code]
function EscapePSLiteral(const Value: string): string;
var S: string;
begin
  S := Value; StringChange(S, '''', ''''''); Result := S;
end;

function RegisterParams(Param: string): string;
var AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  Result := '-NoProfile -ExecutionPolicy Bypass -Command "Add-AppxPackage -Path ''' +
    EscapePSLiteral(AppDir + '\MyApp.identity.msix') +
    ''' -ExternalLocation ''' + EscapePSLiteral(AppDir) + '''"';
end;
```

See the [sparse-app](../../samples/sparse-app) sample for a complete, working `setup.iss`.

The WiX and NSIS examples below invoke a small `register-sparse.ps1` via `-File` so the install path is passed as a **parameter** (PowerShell binds it as data) instead of being interpolated into a `-Command` string. This avoids script injection through a crafted install directory (e.g. a folder name containing a quote or `$(...)`):

```powershell
# register-sparse.ps1 — ship this alongside your installer
param(
  [Parameter(Mandatory)] [string] $MsixPath,
  [Parameter(Mandatory)] [string] $ExternalLocation
)
Add-AppxPackage -Path $MsixPath -ExternalLocation $ExternalLocation
```

### WiX (v3)

```xml
<CustomAction Id="RegisterSparse" Directory="INSTALLFOLDER" Execute="deferred" Impersonate="no"
  ExeCommand="powershell.exe -NoProfile -ExecutionPolicy Bypass -File &quot;[INSTALLFOLDER]register-sparse.ps1&quot; -MsixPath &quot;[INSTALLFOLDER]MyApp.identity.msix&quot; -ExternalLocation &quot;[INSTALLFOLDER]&quot;" />
```

### NSIS

```nsis
Section
  ExecWait 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\register-sparse.ps1" -MsixPath "$INSTDIR\MyApp.identity.msix" -ExternalLocation "$INSTDIR"'
SectionEnd
```

## Troubleshooting

**`Package.Current` throws / "no package identity" at runtime**
- The identity package isn't registered, or the exe's fusion manifest is missing the `<msix>` element. Re-run [`winapp embed-identity`](../usage.md#embed-identity) (and rebuild if using XML mode), then re-register with `Add-AppxPackage -ExternalLocation`.
- The `<msix packageName>` / `publisher` / `applicationId` in the exe must **exactly** match the registered package's identity.

**Assets/logos don't appear**
- Ensure the `Assets/` folder is deployed at the external location with the same relative paths the manifest expects. Assets are resolved from the external location, not the `.msix`.

**`Add-AppxPackage` fails with a signing / trust error**
- The `.msix` must be signed by a certificate that is trusted on the machine and whose subject matches the manifest `Publisher`. For local testing, generate and trust a dev certificate with [`winapp cert generate`](../usage.md#cert), and make sure the manifest `Publisher` matches it.

**MakeAppx: "Application with RuntimeBehavior value 'win32App' must not declare EntryPoint"**
- A sparse `win32App` application must not declare `EntryPoint`. Manifests generated by `winapp init --sparse` are already correct; remove any `EntryPoint` attribute if you hand-edited the manifest.

**"Input is a file but not a sparse manifest"**
- `winapp pack <file>` only accepts a manifest that declares `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`. Generate one with `winapp init --exe <exe> --sparse`, or pass an input *folder* to build a full MSIX.

## See also

- [CLI usage: `init`](../usage.md#init), [`pack`](../usage.md#pack), [`embed-identity`](../usage.md#embed-identity)
- [Grant identity to non-packaged apps (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- [Debugging with package identity](../debugging.md)
