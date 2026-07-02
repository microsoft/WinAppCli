# Sparse Packaging Sample (WPF)

A minimal WPF app that demonstrates the **production sparse packaging** workflow with the `winapp` CLI: grant [package identity](https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview) to an ordinary desktop `.exe` by shipping a tiny identity-only `.msix` and registering it against the app's install directory — without moving your binaries into an MSIX.

The window queries `Windows.ApplicationModel.Package.Current` and shows the package family name when identity is present, or **"No package identity"** when running unpackaged.

> Full background and troubleshooting: [Sparse Packaging Guide](../../docs/guides/sparse.md).

## What's here

```
sparse-app/
├── sparse-app.csproj      # WPF project (references app.manifest as the SxS manifest)
├── App.xaml / .cs
├── MainWindow.xaml / .cs  # Displays package identity status
├── app.manifest           # Side-by-side manifest containing the <msix> identity element (XML mode)
├── appxmanifest.xml       # Pre-generated sparse manifest (AllowExternalContent, win32App)
├── Assets/                # Visual assets — deployed at the EXTERNAL location, not in the .msix
└── installer/setup.iss    # Inno Setup script: install app + register sparse MSIX
```

## Prerequisites

- **Windows 10, version 2004 (build 19041)+** — required for sparse packaging.
- **.NET SDK** (10.0+).
- **winapp CLI** — `winget install Microsoft.WinApp --source winget`.

## Walkthrough

Run these from the `sparse-app` directory.

### 1. Build the app

```powershell
dotnet build
```

### 2. Generate the sparse identity manifest (optional — one is checked in)

The repo ships a ready-made `appxmanifest.xml`. To regenerate it from the built exe:

```powershell
winapp init --exe .\bin\Debug\net10.0-windows10.0.19041.0\sparse-app.exe --sparse --use-defaults
```

> This **skips SDK installation** — sparse identity packages have no SDK dependencies. It only writes `appxmanifest.xml` and `Assets/`.

### 3. Generate a development certificate

```powershell
winapp cert generate
```

The certificate subject must match the manifest `Publisher` (`CN=Sparse App Sample`). Pass `--publisher "CN=Sparse App Sample"` if needed, and install/trust the cert for local testing (`winapp cert install .\devcert.pfx`, admin).

### 4. Pack the identity-only MSIX

```powershell
winapp pack .\appxmanifest.xml --cert .\devcert.pfx
```

Produces `SparseAppSample.identity.msix` — just the manifest, no binaries or assets.

### 5. Embed identity into the exe

The checked-in `app.manifest` already contains the `<msix>` element (XML mode), so a normal `dotnet build` embeds it. To (re)generate it, or to embed directly into a built exe:

```powershell
# XML mode — update the checked-in side-by-side manifest, then rebuild
winapp embed-identity .\app.manifest --manifest .\appxmanifest.xml
dotnet build

# — or — EXE mode: embed straight into an already-built exe
winapp embed-identity .\bin\Debug\net10.0-windows10.0.19041.0\sparse-app.exe --manifest .\appxmanifest.xml
```

### 6. Register the package (development)

```powershell
Add-AppxPackage -Path .\SparseAppSample.identity.msix `
  -ExternalLocation (Resolve-Path .\bin\Debug\net10.0-windows10.0.19041.0)
```

### 7. Run and verify

```powershell
.\bin\Debug\net10.0-windows10.0.19041.0\sparse-app.exe
```

The window should show **"Running with package identity"** and the package family name. If it shows "No package identity", re-check steps 5–6.

### 8. Clean up

```powershell
Get-AppxPackage SparseAppSample* | Remove-AppxPackage
```

## Asset handling

The `.msix` is **identity-only**. The images in `Assets/` are resolved from the **external location** (the install directory) at runtime — they are **not** bundled into the MSIX. Your installer must deploy `Assets/` alongside the app.

## (Optional) Build the installer

The `installer/setup.iss` script produces a `setup.exe` that installs the app, copies the `.msix`, and registers the sparse package automatically — the full production flow.

```powershell
# 1. Publish the app
dotnet publish -c Release -r win-x64 --self-contained false

# 2. Build the identity MSIX (signed)
winapp cert generate
winapp pack .\appxmanifest.xml --cert .\devcert.pfx

# 3. Compile the installer (requires Inno Setup: https://jrsoftware.org/isdl.php)
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\setup.iss
```

The generated installer registers the package on install (`Add-AppxPackage -ExternalLocation`) and unregisters it on uninstall (`Remove-AppxPackage`).
