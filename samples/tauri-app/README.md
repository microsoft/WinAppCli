# Tauri Sample Application

This sample demonstrates how to use winapp CLI with a Tauri application to add package identity, use Windows APIs, and package as MSIX.

For a complete step-by-step guide, see the [Tauri Getting Started Guide](../../docs/guides/tauri.md).

## What This Sample Shows

- Tauri desktop application (Rust + vanilla HTML/JS)
- Using Windows `ApplicationModel` APIs from Rust to retrieve package identity
- Sending Windows toast notifications via `windows::UI::Notifications`
- Conditionally enabling UI features based on package identity status
- MSIX packaging with app manifest and assets

## Prerequisites

- [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/) (Rust, system dependencies)
- Node.js
- winapp CLI installed via winget: `winget install Microsoft.winappcli --source winget`

## Building and Running

### First Time Setup

```powershell
# Install npm dependencies
npm install

# Generate a dev certificate (first time only)
winapp cert generate --if-exists skip
```

### Run

```powershell
# Build the Rust backend
cargo build --manifest-path src-tauri/Cargo.toml

# Stage the exe (the target\debug folder contains build artifacts that aren't needed)
mkdir -Force dist | Out-Null
copy .\src-tauri\target\debug\tauri-app.exe .\dist\

# Run with identity
winapp run .\dist
```

Or use the npm script which does all of the above:

```powershell
npm run tauri:dev:withidentity
```

The Tauri window will display the Package Family Name. The "Send Notification" button is enabled only when the app has package identity — click it to send a Windows toast notification.

### Package as MSIX

```powershell
npm run pack:msix
```

This builds in release mode, stages the exe to `dist/`, and packages+signs it. Then install:

```powershell
# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx
```

Double-click the generated `.msix` file to install. The app will be available in your Start Menu.
