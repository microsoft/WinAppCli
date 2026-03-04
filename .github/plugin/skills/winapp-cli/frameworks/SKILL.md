---
name: winapp-frameworks
description: Framework-specific Windows development guidance for Electron, .NET (WPF, WinForms), C++, Rust, Flutter, and Tauri. Use when packaging or adding Windows features to an Electron app, .NET desktop app, Flutter app, Tauri app, Rust app, or C++ app.
version: 0.2.1
---
## When to use

Use this skill when:
- **Working with a specific app framework** and need to know the right winapp workflow
- **Choosing the correct install method** (npm package vs. standalone CLI)
- **Looking for framework-specific guides** for step-by-step setup, build, and packaging

Each framework has a detailed guide — refer to the links below rather than trying to guess commands.

## Framework guides

| Framework | Install method | Guide |
|-----------|---------------|-------|
| **Electron** | `npm install --save-dev @microsoft/winappcli` | [Electron setup guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/setup.md) |
| **.NET** (WPF, WinForms, Console) | `winget install Microsoft.winappcli` | [.NET guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/dotnet.md) |
| **C++** (CMake, MSBuild) | `winget install Microsoft.winappcli` | [C++ guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/cpp.md) |
| **Rust** | `winget install Microsoft.winappcli` | [Rust guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/rust.md) |
| **Flutter** | `winget install Microsoft.winappcli` | [Flutter guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/flutter.md) |
| **Tauri** | `winget install Microsoft.winappcli` | [Tauri guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/tauri.md) |

## Key differences by framework

### Electron (npm package)
Use the **npm package** (`@Microsoft/WinAppCli`), **not** the standalone CLI. The npm package includes:
- The native winapp CLI binary bundled inside `node_modules`
- A Node.js SDK with helpers for creating native C#/C++ addons
- Electron-specific commands under `npx winapp node`

Quick start:
```powershell
npm install --save-dev @microsoft/winappcli
npx winapp init --use-defaults
npx winapp node create-addon --template cs   # create a C# native addon
npx winapp node add-electron-debug-identity  # register identity for debugging
```

Additional Electron guides:
- [Packaging guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/packaging.md)
- [C++ notification addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/cpp-notification-addon.md)
- [WinML addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/winml-addon.md)
- [Phi Silica addon guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/electron/phi-silica-addon.md)

### .NET (WPF, WinForms, Console)
.NET projects have direct access to Windows APIs. Key differences:
- Projects with NuGet references to `Microsoft.Windows.SDK.BuildTools` or `Microsoft.WindowsAppSDK` **don't need `winapp.yaml`** — winapp auto-detects SDK versions from the `.csproj`
- The key prerequisite is `appxmanifest.xml`, not `winapp.yaml`
- No native addon step needed — unlike Electron, .NET can call Windows APIs directly

Quick start:
```powershell
winapp init --use-defaults
dotnet build
winapp create-debug-identity ./bin/Debug/net10.0-windows/myapp.exe
```

### C++ (CMake, MSBuild)
C++ projects use winapp primarily for SDK projections (CppWinRT headers) and packaging:
- `winapp init --setup-sdks stable` downloads Windows SDK + App SDK and generates CppWinRT headers
- Headers generated in `.winapp/generated/include`
- Response file at `.cppwinrt.rsp` for build system integration
- Add `.winapp/packages` to include/lib paths in your build system

### Rust
- Use the `windows` crate for Windows API bindings
- winapp handles manifest, identity, packaging, and certificate management
- Typical build output: `target/release/myapp.exe`

### Flutter
- Flutter handles the build (`flutter build windows`)
- winapp handles manifest, identity, packaging
- Build output: `build\windows\x64\runner\Release\`

### Tauri
- Tauri has its own bundler for `.msi` installers
- Use winapp specifically for **MSIX distribution** and package identity features
- winapp adds capabilities beyond what Tauri's built-in bundler provides (identity, sparse packages, Windows API access)
