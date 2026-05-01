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
- The key prerequisite is `Package.appxmanifest`, not `winapp.yaml`
- No native addon step needed — unlike Electron, .NET can call Windows APIs directly
- `winapp init` automatically adds the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, enabling `dotnet run` with automatic identity registration

**If you already have a `Package.appxmanifest`** (e.g., WinUI 3 apps or projects with an existing packaging setup), you likely **don't need `winapp init`** — your project is already configured for packaged builds. Just make sure:
- Your `.csproj` references the `Microsoft.WindowsAppSDK` NuGet package (WinUI 3 apps already have this)
- The project properties are set up for packaged builds (e.g., `<WindowsPackageType>MSIX</WindowsPackageType>` or equivalent)
- WinUI 3 apps created from Visual Studio templates are typically already fully configured

Quick start:
```powershell
winapp init --use-defaults
dotnet build <path-to-project.csproj> -c Debug -p:Platform=x64
winapp run bin\x64\Debug\<tfm>\win-x64\
```

Replace `<tfm>` with your target framework (e.g., `net10.0-windows10.0.26100.0`), and adjust `x64` to match your target architecture.

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

## Debugging by framework

| Framework | Recommended command | Notes |
|-----------|-------------------|-------|
| **.NET** | `winapp run .\bin\x64\Debug\<tfm>\win-x64\` | Build with `dotnet build -c Debug -p:Platform=x64` first; GUI apps launch directly; console apps need `--with-alias` |
| **C++** | `winapp run .\build\Debug --with-alias` | Console apps need `--with-alias` + `uap5:ExecutionAlias` in manifest |
| **Rust** | `winapp run .\target\debug --with-alias` | Console apps need `--with-alias` + `uap5:ExecutionAlias` in manifest |
| **Flutter** | `winapp run .\build\windows\x64\runner\Debug` | GUI app — plain `winapp run` works |
| **Tauri** | `winapp run .\dist` | Stage exe to `dist/` first (avoids copying entire `target/` tree); GUI app |
| **Electron** | `npx winapp node add-electron-debug-identity` | Uses Electron-specific identity registration; `winapp run` is **not** recommended for Electron |

**Key rules:**
- **GUI apps** (Flutter, Tauri, WPF): use `winapp run <build-output>` — launches via AUMID activation
- **Console apps** (C++, Rust, .NET console): use `winapp run <build-output> --with-alias` — launches via execution alias to preserve stdin/stdout. Requires `uap5:ExecutionAlias` in `Package.appxmanifest`
- **Electron**: different mechanism — uses `npx winapp node add-electron-debug-identity` because `electron.exe` is in `node_modules/`, not your build output
- **Startup debugging (any framework)**: use `winapp create-debug-identity <exe>` so your IDE can F5-launch the exe with identity from the first instruction

For full debugging scenarios and IDE setup, see the [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md).

## Related skills
- **Setup**: `winapp-setup` — initial project setup with `winapp init`
- **Manifest**: `winapp-manifest` — creating and customizing `Package.appxmanifest`
- **Signing**: `winapp-signing` — certificate generation and management
- **Packaging**: `winapp-package` — creating MSIX installers from build output
- **Identity**: `winapp-identity` — enabling package identity for Windows APIs during development
- Not sure which command to use? See `winapp-troubleshoot` for a command selection flowchart
