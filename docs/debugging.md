# Debugging with Package Identity

Many Windows APIs (push notifications, background tasks, share target, startup tasks, Windows AI APIs) require your app to have **package identity**. During development, you don't want to build a full MSIX installer every time you test — winapp provides two commands to give your app identity on the fly.

> **Using Visual Studio with a packaging project?** If you are already using Visual Studio for your packaged project, you likely don't need winapp for debugging. Visual Studio already handles package registration, identity, AUMID activation, debugger attachment, and activation-code debugging — all from F5. It also offers **Debug → Other Debug Targets → Debug Installed App Package** for advanced scenarios. The workflows below are most useful for **VS Code users, terminal-based workflows, and frameworks that VS doesn't natively package** (Rust, Flutter, Tauri, Electron, plain C++).

## Two approaches: `winapp run` vs `create-debug-identity`

| | `winapp run` | `create-debug-identity` |
|---|---|---|
| **What it registers** | Full loose layout package (entire folder) | Sparse package (single exe) |
| **How the app launches** | Launched by winapp (AUMID activation or execution alias) | You launch the exe yourself (command line, IDE, etc.) |
| **Simulates MSIX install** | Yes — closest to production behavior | No — sparse identity only |
| **Files stay in place** | Copied to an AppX layout directory | Yes — exe stays at its original path |
| **Identity scope** | Entire folder contents (exe, DLLs, assets) | Single executable |
| **Debugger-friendly** | Attach to PID after launch, or use `--no-launch` then launch via alias | Launch directly from your IDE's debugger — the exe has identity regardless |
| **Console app support** | `--with-alias` keeps stdin/stdout in terminal | Run exe directly in terminal |
| **Best for** | Most frameworks (.NET, C++, Rust, Flutter, Tauri) | Electron, or when you need full IDE debugger control (F5) |

## When to use which

### Default: `winapp run`

Use `winapp run` for most development workflows. It simulates a real MSIX install — your app gets the same identity, capabilities, and file associations it would have in production.

```powershell
# Build your app, then:
winapp run .\build\output
```

### Use `create-debug-identity` when:

- **Your exe is separate from your build output** — e.g., Electron apps where `electron.exe` lives in `node_modules/`
- **You need to debug startup code** and can't attach a debugger fast enough after AUMID launch
- **With some debuggers where you can't launch with AUMID** and need identity on the launched process — `create-debug-identity` registers the exe so it has identity no matter how it's started
- **You're testing sparse package behavior** specifically (AllowExternalContent, TrustedLaunch)

```powershell
# Register identity for an exe, then launch it however you want:
winapp create-debug-identity .\bin\Debug\myapp.exe
.\bin\Debug\myapp.exe   # or F5 in your IDE
```

## Debugging scenarios

### Scenario A: Just run with identity

The simplest workflow — build, run with identity, done.

```powershell
winapp run .\build\Debug
```

Winapp registers the folder as a loose layout package and launches the app. Identity-requiring APIs work immediately. This covers the majority of development and testing scenarios.

For **console apps** that need stdin/stdout in the current terminal, add `--with-alias`:

```powershell
winapp run .\build\Debug --with-alias
```

### Scenario B: Attach a debugger to a running app

Launch with `winapp run`, note the PID, then attach your IDE's debugger.

```powershell
winapp run .\build\Debug
# Output: Process ID: 12345
```

Then in your IDE:
- **VS Code**: Run and Debug → select "Attach" configuration (see [IDE setup](#ide-setup) below)
- **WinDbg**: `windbg -p 12345`

> **Limitation:** You'll miss any code that runs before you attach. For startup debugging, use Scenario D (`create-debug-identity`).

### Scenario C: Register identity, then launch via AUMID or alias from your IDE

Use `--no-launch` to register the package, then launch the app through its AUMID (reported by `run`) or **execution alias** from your IDE.

**Step 1:** Register the package without launching:

```powershell
winapp run .\build\Debug --no-launch
```

**Step 2:** Configure your IDE to launch via the AUMID or the **execution alias** (not the exe directly). 
* Launching with AUMID: Use the command `start shell:AppsFolder\<AUMID>`. `winapp run` outputs the AUMID when the app is registered.
* Launching with the alias: The alias must be defined in the appxmanifest.xml (or Package.appxmanifest).

> **Important:** Simply launching the exe in the build folder will **not** give it identity. The app must be started via AUMID activation or its execution alias. This is how loose layout packages work - identity is tied to the activation path, not the exe file.

### Scenario D: Launch from your IDE with identity (startup debugging)

This is the best approach for **debugging startup code with full IDE control** - your IDE's debugger controls the process from the very first instruction, and the exe has identity no matter how it's launched.

```powershell
winapp create-debug-identity .\build\Debug\myapp.exe
```

Now launch the exe any way you like — from the terminal, from VS Code's F5, from a script. The exe has identity because Windows registered a **sparse package** pointing directly at it.

> **How it differs from `winapp run`:** With `create-debug-identity`, identity is tied to the exe itself (via `Add-AppxPackage -ExternalLocation`). With `winapp run`, identity is tied to the loose layout package — the app must be launched through AUMID or an alias. This makes `create-debug-identity` the better choice when you need your IDE to launch and debug the exe directly.

> This is also the best approach for **Electron apps** where the exe path differs from your source directory.

### Scenario E: Capture debug output and crash diagnostics

Capture `OutputDebugString` messages and first-chance exceptions inline. Framework noise (WinUI, COM, DirectX internal traces) is filtered from the console so only your app's debug messages appear. Everything is still written to the log file for full investigation.

If the app crashes, a minidump is captured and analyzed automatically:

```powershell
winapp run .\build\Debug --debug-output
```

On crash, the output includes the exception type, message, and stack trace with source file and line numbers (resolved from PDBs in your build output folder). Managed (.NET) crashes are analyzed instantly with no external tools. Native (C++/WinRT) crashes show module names and offsets; add `--symbols` to download PDB symbols for full function names:

```powershell
winapp run .\build\Debug --debug-output --symbols
```

> **Important:** This attaches winapp as the debugger. Windows only allows one debugger per process, so you **cannot** also attach Visual Studio, VS Code, or WinDbg.

## IDE setup

### VS Code

**Attach to running process** — add to `.vscode/launch.json`:

```json
{
    "name": "Attach to Process",
    "type": "coreclr",
    "request": "attach",
    "processId": "${command:pickProcess}"
}
```

For C++/Rust, use `"type": "cppvsdbg"` (MSVC) or `"type": "lldb"` (LLDB):

```json
{
    "name": "Attach (C++)",
    "type": "cppvsdbg",
    "request": "attach",
    "processId": "${command:pickProcess}"
}
```
