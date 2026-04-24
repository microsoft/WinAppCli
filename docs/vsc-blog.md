# Announcing the WinApp VS Code Extension — Run, Debug, and Package Windows Apps in VS Code

VS Code is where many cross-platform and web developers already work, but getting package identity, MSIX packaging, and Windows SDK tooling meant reaching for Visual Studio or command-line tools. The **WinApp VS Code extension** brings the full power of the [Windows App Development CLI](https://github.com/microsoft/WinAppCli) directly into VS Code, so you can initialize, run, debug, package, and sign Windows applications without ever leaving the editor. 

Whether you're building with **.NET, WPF, WinUI, C++, Electron, Rust, Tauri, or Flutter** this extension is for you. The **WinApp VS Code extension** is now available in public preview on the [Visual Studio Code Marketplace](https://marketplace.visualstudio.com/items?itemName=TODO-PLACEHOLDER).

Let's walk through what's included.

## 🎨 Command Palette Commands

Many of the WinApp CLI commands are available from the VS Code Command Palette (`Ctrl+Shift+P`). Type **WinApp** and you'll see the full list:

- **Initialize Project**: configure your project with the Windows SDK and/or Windows App SDK
- **Restore / Update Packages**: manage project dependencies
- **Run Application**: launch your app as a loose-layout packaged app with full package identity
- **Create Debug Identity**: add sparse package identity to an existing executable for F5 debugging
- **Unregister Package**: clean up sideloaded development packages when you're don
- **Create MSIX Package**: package your app into an MSIX, with options for certificates and self-contained runtime
- **Generate Manifest**: create an `Package.manifest` from a template
- **Add Manifest Execution Alias**: add a command-line alias so your packaged app can be launched by name
- **Update Manifest Assets**: auto-generate all required app icons from a single source image
- **Generate / Install Certificate**: create or install development certificates for signing
- **Certificate Info**: display certificate details (subject, thumbprint, expiry) to verify a certificate matches your manifest
- **Sign Package**: sign an MSIX package or executable
- **Run SDK Tool**: run `makeappx`, `signtool`, `mt`, or `makepri` with custom arguments
- **Get WinApp Path**: show paths to installed SDK components

No separate CLI installation required. The WinApp CLI is bundled with the extension.

## 🐛 Integrated Launching and Debugging with Package Identity

Many Windows APIs — notifications, background tasks, on-device AI, share targets — require your app to have **package identity**. Traditionally, getting identity meant building a full MSIX installer or running from Visual Studio. The WinApp extension changes that.

The extension provides a custom **`winapp` debug type** that gives your app package identity and attaches your debugger, all from a single **F5** press.

**How it works:**

1. Press **F5** (or start a debug session)
2. The extension locates your build output and manifest
3. It launches your app via `winapp run` to give it package identity
4. A child debug session attaches using your preferred debugger

**Supported debuggers:**

| `debuggerType` | Language | Required Extension |
|----------------|----------|--------------------|
| `coreclr` (default) | C# / .NET | [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp) |
| `cppvsdbg` | C / C++ | [C/C++](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools) |
| `node` | Node.js / Electron | Built-in |

Getting started is as simple as adding a `winapp` configuration to your `launch.json`:

```jsonc
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "winapp",
            "request": "launch",
            "name": "WinApp: Launch and Attach"
        }
    ]
}
```

### Automate the build step

The `winapp` debug type assumes your project has already been built. It **does not** build automatically. After making code changes, you need to rebuild before pressing F5. 

The good news: you can automate this with a `preLaunchTask` so your project is always built before every debug session.

**1. Define a build task** in `.vscode/tasks.json` (example for .NET):

```jsonc
{
    "version": "2.0.0",
    "tasks": [
        {
            "label": "build",
            "command": "dotnet",
            "type": "process",
            "args": ["build", "${workspaceFolder}"],
            "problemMatcher": "$msCompile"
        }
    ]
}
```

**2. Reference it** in your `launch.json`:

```jsonc
{
    "type": "winapp",
    "request": "launch",
    "name": "WinApp: Launch and Attach",
    "preLaunchTask": "build"
}
```

Now every time you press F5, VS Code will build your project first, then launch it with package identity and attach the debugger, just like the full Visual Studio experience.

See the full [Debugging Guide](https://github.com/microsoft/WinAppCli/blob/main/docs/debugging.md) for more details.

## 🧰 Works with Any Windows App Framework

The extension works with the same broad set of frameworks as the WinApp CLI:

- **.NET**: WPF, WinForms, Console, WinUI3
- **C / C++**: Win32, CMake, MSBuild
- **Electron** / **Node.js**
- **Rust**
- **Tauri**
- **Flutter**

If it builds to a Windows desktop app, the WinApp extension can help you package, debug, and ship it.

## 🚀 Get Started

**Install from the VS Code Marketplace:**

1. Open VS Code
2. Go to the Extensions view (`Ctrl+Shift+X`)
3. Search for **WinApp**
4. Click **Install**

Or install from the command line:

```
code --install-extension Microsoft-WinAppCLI.winapp
```

**Requirements:**

- Windows 10 or later
- Visual Studio Code 1.109.0 or later
- For debugging, install the debugger extension that matches your app's language (see supported debuggers above)

Once installed, open a Windows app project, hit `Ctrl+Shift+P`, type **WinApp**, and start exploring.

Head over to [WinApp VS Code Extension](https://github.com/microsoft/winappCli/blob/main/src/winapp-VSC/README.md) for full documentation.

## 💬 We Want Your Feedback

This is a **public preview**; we're actively developing the extension and want to hear from you. Your feedback directly shapes what we build next.

- **Found a bug?** [File an issue](https://github.com/microsoft/WinAppCli/issues)
- **Have a feature request?** [Open an issue](https://github.com/microsoft/WinAppCli/issues) and tell us what would make your workflow better
- **Something confusing?** Let us know. We want the experience to be smooth from day one

Want to see what we're working on? Check out our [open VS Code extension issues](https://github.com/microsoft/winappCli/issues?q=is%3Aissue%20state%3Aopen%20label%3Avs-code-extension) on GitHub. Feel free to upvote, comment, or open new issues for features you'd like to see.

Happy coding! 🎉
