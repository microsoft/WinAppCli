# winapp CLI

> [!IMPORTANT]
> :warning: **Status: Public Preview** - The Windows App Development CLI (winapp CLI) is experimental and in active development. We'd love your feedback! Share your thoughts by creating an [issue](https://github.com/microsoft/WinAppCli/issues).

<p align="center">
    <picture>
      <img  src="./docs/images/winapp-terminal.png">
    </picture>
</p>
<br/>
<p align="center">
  <img src="https://img.shields.io/winget/v/Microsoft.WinAppCli?style=for-the-badge&logo=windows&color=357EC7" alt="WinGet">
  <a href="https://www.npmjs.com/package/@microsoft/winappcli">
    <img src="https://img.shields.io/npm/v/%40microsoft%2Fwinappcli?style=for-the-badge&logo=npm" alt="NPM">
  </a>
  <a href="https://github.com/microsoft/WinAppCli/releases/latest">
    <img src="https://img.shields.io/github/v/release/microsoft/WinAppCli?style=for-the-badge&logo=github&label=Latest%20Release&color=8ab4f8" alt="Latest Release">
  </a>
  <br />
  <a href="https://github.com/microsoft/WinAppCli/issues">
    <img src="https://img.shields.io/github/issues/microsoft/WinAppCli?style=for-the-badge&logo=github&color=81c995" alt="Issues">
  </a>
  <a href="https://github.com/microsoft/WinAppCli/blob/main/LICENSE">
    <img alt="GitHub License" src="https://img.shields.io/github/license/microsoft/winappcli?style=for-the-badge">
  </a>
  <br />
</p>

<h3 align="center">
  <a href="#-why-package-identity">Why?</a>
  <span> • </span>
  <a href="#%EF%B8%8F-get-started">Get Started</a>
  <span> • </span>
  <a href="#-installation">Installation</a>
  <span> • </span>
  <a href="#-usage">Usage</a>
  <span> • </span>
  <a href="./docs/usage.md">Docs</a>
  <span> • </span>
  <a href="#-feedback-and-support">Feedback</a>
</h3>
<br/>

The Windows App Development CLI (winapp CLI) is a single command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework. This tool bridges the gap between cross-platform development and Windows-native capabilities.
<br/><br/>
Whether you're building with .NET/Win32, CMake, Electron, or Rust, this CLI gives you access to:

- **Modern Windows APIs** - [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) and Windows SDK with automatic setup and code generation
- **Package Identity** - Debug and test by adding package identity without full packaging in a snap
- **MSIX Packaging** - App packaging with signing and Store readiness
- **Developer Tools** - Manifests, certificates, assets, and build integration

Perfect for:

- **Cross-platform developers using frameworks like Qt or Electron** wanting native Windows features or targeting Windows
- **Developers who love their current tools** and want to build Windows apps from VS Code, or any other editor
- **Developers crafting CI/CD pipelines** to automate building apps for Windows

## 🤔 Why?

Many powerful Windows APIs require your app to have package identity, enabling you to leverage some of the OS components Windows offers, that you wouldn't otherwise have access to. With identity, your app gains access to user-first features like notifications, OS integration, and on-device AI.

Our goal is to support developers wherever they are, with the tools and frameworks they already use. Based on feedback from developers shipping cross-platform apps on Windows, we built this CLI to streamline integrating with the Windows developer platform - handling SDK setup, header generation, manifests, certificates, and packaging in just a few commands:


<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="./docs/images/before-after-winapp-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="./docs/images/before-after-winapp.png">
    <img src="./docs/images/before-after-winapp.png" alt="Before: 12 manual steps to access Windows APIs. After: 4 winapp commands (init, create-addon, add-electron-debug-identity, pack)">
  </picture>
</p>
<p align="center"><i>Without winapp CLI, setting up a project involves 12 manual steps—downloading SDKs, generating headers, creating manifests, and more. With the CLI, it's just 4 commands.</i></p>

**Few examples of what package identity and MSIX packaging unlocks:**

- [Interactive native notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart?tabs=cs) and notification management
- [Integration with Windows Explorer, Taskbar, Share sheet](https://learn.microsoft.com/windows/apps/develop/windows-integration/integrate-sharesheet-packaged), and other shell surfaces
- [Protocol handlers](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-your-application-in-different-ways) (`yourapp://` URIs)
- [Web-to-app linking](https://learn.microsoft.com/windows/apps/develop/launch/web-to-app-linking) (`yoursite.com` opens your app)
- [On-device AI](https://learn.microsoft.com/windows/ai/apis/) (Local LLM, Text and Image AI APIs)
- [Custom CLI commands via AppExecutionAlias](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-your-application-in-different-ways)
- [Controlled access to camera, microphone, location](https://learn.microsoft.com/windows/uwp/packaging/app-capability-declarations), and other devices (with user consent)
- [Background tasks](https://learn.microsoft.com/windows/uwp/launch-resume/declare-background-tasks-in-the-application-manifest) (run when app is closed)
- [File type associations](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#integrate-with-file-explorer) (open `.xyz` files with your app)
- [Startup tasks](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-an-executable-file-when-users-log-into-windows) (launch at Windows login)
- [App services](https://learn.microsoft.com/windows/uwp/launch-resume/how-to-create-and-consume-an-app-service) (expose APIs to other apps)
- [Clean install/uninstall & auto-updates](https://learn.microsoft.com/windows/msix/overview)

## ✏️ Get started

Checkout our getting started guides for step by step instructions of how to setup your environment, generate manifests, assets, and certificate, how to debug APIs that require package identity, and how to MSIX package your app.

<p>
  <a href="./docs/guides/dotnet.md">
    <img src="https://img.shields.io/badge/.NET/WPF/WinForms-Get%20Started-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt="Get Started with .NET">
  </a>
    <br />
  <a href="./docs/guides/cpp.md">
    <img src="https://img.shields.io/badge/C++-Get%20Started-00599C?style=for-the-badge&logo=cplusplus&logoColor=white" alt="Get Started with C++">
  </a>
    <br />
  <a href="/docs/electron-get-started.md">
    <img src="https://img.shields.io/badge/Electron-Get%20Started-47848F?style=for-the-badge&logo=electron&logoColor=white" alt="Get Started with Electron">
  </a>
    <br />
  <a href="./docs/guides/rust.md">
    <img src="https://img.shields.io/badge/Rust-Get%20Started-000000?style=for-the-badge&logo=rust&logoColor=white" alt="Get Started with Rust">
  </a>
    <br />
  <a href="/docs/guides/tauri.md">
    <img src="https://img.shields.io/badge/Tauri-Get%20Started-FFC131?style=for-the-badge&logo=tauri&logoColor=black" alt="Get Started with Tauri">
  </a>
</p>

Additional guides:
- [Packaging an EXE/CLI](/docs/guides/packaging-cli.md): step by step guide of packaging an existing exe/cli as MSIX

## 📦 Installation

### WinGet <img src="https://img.shields.io/winget/v/Microsoft.WinAppCli?style=for-the-badge&logo=windows&color=357EC7" alt="WinGet" height="24">

The easiest way to use the CLI is via WinGet (Windows Package Manager). In Terminal, simply run:

`winget install Microsoft.winappcli --source winget`

### NPM <a href="https://www.npmjs.com/package/@microsoft/winappcli"> <img src="https://img.shields.io/npm/v/%40microsoft%2Fwinappcli?style=for-the-badge&logo=npm" alt="NPM" height="24"></a>


You can install the CLI for Electron projects via NPM:

`npm install @microsoft/winappcli --save-dev`

### GitHub Actions / Azure DevOps

For CI/CD pipelines on GitHub Actions or Azure DevOps, use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli?tab=readme-ov-file#setup-windows-app-developer-cli) action to automatically install the CLI on your runners/agents.

### Download Release Manually

**[Download the latest build from GitHub Releases](https://github.com/microsoft/WinAppCli/releases/latest)**

## 📋 Usage

Once installed (see [Installation](#-installation) above), verify the installation by calling the CLI:

```bash
winapp --help
```

or if using Electron/Node.js

```bash
npx winapp --help
```

### Commands Overview

**Setup Commands:**

- [`init`](./docs/usage.md#init) - Initialize project with Windows SDK and App SDK
- [`restore`](./docs/usage.md#restore) - Restore packages and dependencies
- [`update`](./docs/usage.md#update) - Update packages and dependencies to latest versions

**App Identity & Debugging:**

- [`package`](./docs/usage.md#package) - Create MSIX packages from directories
- [`create-debug-identity`](./docs/usage.md#create-debug-identity) - Add temporary app identity for debugging
- [`manifest`](./docs/usage.md#manifest) - Generate and manage AppxManifest.xml files

**Certificates & Signing:**

- [`cert`](./docs/usage.md#cert) - Generate and install development certificates
- [`sign`](./docs/usage.md#sign) - Sign MSIX packages and executables

**Development Tools:**

- [`tool`](./docs/usage.md#tool) - Access Windows SDK tools
- [`get-winapp-path`](./docs/usage.md#get-winapp-path) - Get paths to installed SDK components

**Node.js/Electron Specific:**

- [`node create-addon`](./docs/usage.md#node-create-addon) - Generate native C# or C++ addons
- [`node add-electron-debug-identity`](./docs/usage.md#node-add-electron-debug-identity) - Add identity to Electron processes
- [`node clear-electron-debug-identity`](./docs/usage.md#node-clear-electron-debug-identity) - Remove identity from Electron processes
- [Windows AI Addon for Electron](https://github.com/microsoft/winapp-windows-ai) 

The full CLI usage can be found here: [Documentation](/docs/usage.md)


## 🧾 Samples

This repository includes samples demonstrating how to use the CLI with various frameworks:

| Sample | Description |
|--------|-------------|
| [C++ App](/samples/cpp-app/README.md) | Native C++ Win32 application with CMake |
| [.NET Console](/samples/dotnet-app/README.md) | .NET console application |
| [WPF App](/samples/wpf-app/README.md) | WPF desktop application |
| [Electron](/samples/electron/README.md) | Electron Forge app with appxmanifest, assets, native C++ addon, and C# addon |
| [Electron WinML](/samples/electron-winml/README.md) | Electron app using Windows ML for image classification |
| [Rust App](/samples/rust-app/README.md) | Rust application using Windows APIs |
| [Tauri App](/samples/tauri-app/README.md) | Tauri cross-platform app with Rust backend |

## 🔧 Feedback and Support

[File an issue, feature request or bug](https://github.com/microsoft/WinAppCli/issues): please ensure that you are not filing a duplicate issue

Need help or have questions about the Windows App Development CLI? Visit our **[Support Guide](./SUPPORT.md)** for information about our issue templates and triage process.

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

To build the CLI:
```
# Build the CLI and package for npm from the repo root
.\scripts\build-cli.ps1
```

The binaries and packages will be placed in the `artifacts` folder

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
