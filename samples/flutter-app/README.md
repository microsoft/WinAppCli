# Flutter Sample Application

This sample demonstrates how to use winapp CLI with a Flutter application to add package identity, use Windows App SDK APIs, and package as MSIX.

For a complete step-by-step guide, see the [Flutter Getting Started Guide](../../docs/guides/flutter.md).

## What This Sample Shows

- Flutter desktop application targeting Windows
- Using Windows APIs (via Dart FFI) to retrieve package identity
- Using Windows App SDK C++ headers from `.winapp/include` to call `RuntimeInfo::AsString()` via a native method channel
- Sending Windows App SDK toast notifications via `AppNotificationBuilder`
- MSIX packaging with app manifest and assets

## Prerequisites

- Flutter SDK
- winapp CLI installed via winget: `winget install Microsoft.winappcli --source winget`

## Building and Running

### First Time Setup

```powershell
# Install dependencies
flutter pub get

# Restore Windows App SDK headers and packages from winapp.yaml
winapp restore

# Generate a dev certificate (first time only)
winapp cert generate --if-exists skip
```

### Run

```powershell
# Build the app
flutter build windows

# Apply debug identity
winapp create-debug-identity .\build\windows\x64\runner\Release\flutter_app.exe

# Run the app
.\build\windows\x64\runner\Release\flutter_app.exe
```

The Flutter window will display:
```
Package Family Name: flutter-app.debug_xxxxxxxx
Windows App Runtime: 8000.731.xxx
```

Click the "Show Notification" button to send a Windows toast notification powered by the Windows App SDK.

### Package as MSIX

```powershell
# Build in release mode
flutter build windows

# Copy build output to dist folder
mkdir dist
copy .\build\windows\x64\runner\Release\* .\dist\ -Recurse

# Package and sign
winapp pack .\dist --cert .\devcert.pfx

# Install certificate (first time only, requires admin)
winapp cert install .\devcert.pfx
```

Double-click the generated `.msix` file to install. The app will be available in your Start Menu.
