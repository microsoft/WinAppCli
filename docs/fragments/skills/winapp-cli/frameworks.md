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
- `node jsbindings add` — generates typed JS/TypeScript WinRT wrappers via dynwinrt (no native build required)

Quick start:
```powershell
npm install --save-dev @microsoft/winappcli
npx winapp init --use-defaults --js-bindings-ai   # init + generate typed AI bindings in bindings/winrt/
# (or, if you already initialized the workspace:)
npx winapp node jsbindings add --ai               # layer JS bindings onto an existing workspace
npx winapp node create-addon --template cs        # create a C# native addon (for what dynwinrt can't drive — see below)
npx winapp node add-electron-debug-identity       # register identity for debugging
```

The `--js-bindings*` flags (and the `node jsbindings add` sub-command) are **npm-only** — they require invocation via the `@microsoft/winappcli` npm package because they pull in `@microsoft/dynwinrt-codegen` as a transitive dep. The winget / standalone install will reject these surfaces with a clear error.

#### Choosing between jsBindings and a native addon

The decision is almost entirely about the **shape of the API**, not preference.

**Default: if the API is WinRT (ships in a `.winmd`), use `node jsbindings add`.** That covers nearly everything an Electron app actually calls on Windows — `Microsoft.Windows.*` (Notifications, FilePickers, Sensors, Storage, AI inference like `TextRecognizer` / `LanguageModel`), most of `Windows.*`, and all of `Microsoft.WindowsAppSDK.AI`. dynwinrt's [own scope statement](https://github.com/microsoft/dynwinrt#scope) sums it up as "non-UI WinRT APIs ... headless services from the Windows SDK and WinAppSDK".

**Fall back to `node create-addon` when one of these is true:**

| Scenario | Template | Why dynwinrt can't help |
|---|---|---|
| The API is **Win32 / pure COM with no WinRT projection** (P/Invoke-style APIs, raw `IFileDialog`, registry, custom COM servers). | `--template cpp` | No `.winmd` exists, so there's nothing for the codegen to project. |
| You're integrating a **C++ library that ships only headers + a static/shared lib** (no `.winmd`). | `--template cpp` | Same — dynwinrt requires WinRT metadata. |
| You're integrating a **vendor SDK that only ships a managed .NET assembly** (no `.winmd`). | `--template cs` (uses [node-api-dotnet](https://github.com/microsoft/node-api-dotnet) under the hood). | Same — no WinRT projection to consume. |

It's normal to mix both in one app: jsBindings for the non-UI WinRT surface, a small C# or C++ addon for the one or two Win32 / non-WinRT calls that don't fit.

Additional Electron guides:
- [JS bindings reference](https://github.com/microsoft/WinAppCli/blob/main/docs/js-bindings.md) — full `jsBindings:` yaml schema, presets, per-package classification, lockfile
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
