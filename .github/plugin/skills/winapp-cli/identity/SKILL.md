---
name: winapp-identity
description: Enable Windows package identity for desktop apps to access Windows APIs like push notifications, background tasks, share target, and startup tasks. Use when adding Windows notifications, background tasks, or other identity-requiring Windows features to a desktop app.
version: 0.2.2
---
## When to use

Use this skill when:
- **Debugging Windows APIs** that require package identity (push notifications, background tasks, share target, startup tasks, etc.)
- **Testing identity-dependent features** without creating and installing a full MSIX package
- **Registering a sparse package** with Windows for development

## Prerequisites

1. **`appxmanifest.xml`** in your project — from `winapp init` or `winapp manifest generate`
2. **Built executable** — the `.exe` your app runs from

## What is package identity?

Windows package identity enables your app to use restricted APIs and OS integration features:
- **Push notifications** (WNS)
- **Background tasks**
- **Share target** / share source
- **App startup tasks**
- **Taskbar pinning**
- **Windows AI APIs** (Phi Silica, OCR, etc.)
- **File type associations** registered properly in Settings

A standard `.exe` (from `dotnet build`, `cmake`, etc.) does **not** have identity. `create-debug-identity` registers a *sparse package* with Windows, giving your exe identity without packaging it into an MSIX.

## Usage

### Basic usage

```powershell
# Register sparse package for your exe (manifest auto-detected from current dir)
winapp create-debug-identity ./bin/Release/myapp.exe

# Specify manifest location
winapp create-debug-identity ./bin/Release/myapp.exe --manifest ./appxmanifest.xml
```

### Keep the original package identity

```powershell
# By default, '.debug' is appended to the package name to avoid conflicts with
# an installed MSIX version. Use --keep-identity to keep the manifest identity as-is.
winapp create-debug-identity ./myapp.exe --keep-identity
```

### Generate without installing

```powershell
# Create the sparse package layout but don't register it with Windows
winapp create-debug-identity ./myapp.exe --no-install
```

## What the command does

1. **Reads `appxmanifest.xml`** — extracts identity, capabilities, and assets
2. **Creates a sparse package layout** in a temp directory
3. **Appends `.debug`** to the package name (unless `--keep-identity`) to avoid conflicts
4. **Registers with Windows** via `Add-AppxPackage -ExternalLocation` — makes your exe "identity-aware"

After running, launch your exe normally — Windows will recognize it as having package identity.

## Recommended workflow

1. **Setup** — `winapp init --use-defaults` (creates `appxmanifest.xml`)
2. **Generate development certificate** — `winapp cert generate`
3. **Build** your app
4. **Register identity** — `winapp create-debug-identity ./bin/myapp.exe`
5. **Run** your app — identity-requiring APIs now work
6. **Re-run step 4** whenever you change `appxmanifest.xml` or `Assets/`

## Tips

- You must re-run `create-debug-identity` after any changes to `appxmanifest.xml` or image assets
- The debug identity persists across reboots until explicitly removed
- To remove: `Get-AppxPackage *yourapp.debug* | Remove-AppxPackage`
- If you have both a debug identity and an installed MSIX, they may conflict — use `--keep-identity` carefully
- For Electron apps, use `npx winapp node add-electron-debug-identity` instead (handles Electron-specific paths)

## Related skills
- Need a manifest? See `winapp-manifest` to generate `appxmanifest.xml`
- Need a certificate? See `winapp-signing` — a trusted cert is required for identity registration
- Ready for full MSIX distribution? See `winapp-package` to create an installer
- Having issues? See `winapp-troubleshoot` for common error solutions

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "appxmanifest.xml not found" | No manifest in current directory | Run `winapp init` or `winapp manifest generate`, or pass `--manifest` |
| "Failed to add package identity" | Previous registration stale or cert untrusted | `Get-AppxPackage *yourapp* \| Remove-AppxPackage`, then `winapp cert install ./devcert.pfx` (admin) |
| "Access denied" | Cert not trusted or permission issue | Run `winapp cert install ./devcert.pfx` as admin |
| APIs still fail after registration | App launched before registration completed | Close app, re-run `create-debug-identity`, then relaunch |


## Command Reference

### `winapp create-debug-identity`

Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires appxmanifest.xml in current directory or passed via --manifest. Re-run after changing appxmanifest.xml or Assets/.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<entrypoint>` | No | Path to the .exe that will need to run with identity, or entrypoint script. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--keep-identity` | Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. | (none) |
| `--manifest` | Path to the appxmanifest.xml | (none) |
| `--no-install` | Do not install the package after creation. | (none) |
