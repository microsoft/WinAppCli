# WinUI 3 Unpackaged Sample Application

This sample demonstrates an **unpackaged** WinUI 3 desktop app — one that runs
without MSIX package identity (`WindowsPackageType=None`). It exists to exercise
`winapp run` **project mode** against an unpackaged target.

## What This Sample Shows

- Unpackaged WinUI 3 desktop app (no `Package.appxmanifest`, no MSIX identity)
- Framework-dependent Windows App SDK (`WindowsAppSDKSelfContained=false`), so the
  app relies on the Windows App Runtime being installed on the machine
- `winapp run` project mode: build **and** launch straight from the `.csproj`

## Building and Running

Point `winapp run` at the project (or the folder containing it) and it builds and
launches in one step — no separate `dotnet build` and no need to find the output
`.exe`:

```powershell
# Build and run the unpackaged app from the project directory
winapp run .

# Or target the project file explicitly, with a configuration/arch
winapp run .\winui-unpackaged-app.csproj -c Debug --arch x64
```

Because the app is framework-dependent, `winapp run` installs the matching
architecture Windows App Runtime (the same runtime a packaged app needs) before
launching the built `.exe` directly. No debug identity is registered for
unpackaged apps.

To build without launching, use the SDK directly:

```powershell
dotnet build -c Debug -p:Platform=x64
```
