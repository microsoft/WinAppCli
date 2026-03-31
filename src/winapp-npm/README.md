# Windows App Development CLI

> **Status: Public Preview** - The Windows App Development CLI is experimental and in active development. We'd love your feedback! Share your thoughts by creating an [issue](https://github.com/microsoft/WinAppCli/issues).

The Windows App Development CLI is a single command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework. The NPM package extends the CLI with Electron specific tools.
<br/><br/>
This CLI gives you access to:

- **Modern Windows APIs** - [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) and Windows SDK with automatic setup and code generation
- **App Identity** - Debug and test by adding app identity without full packaging in a snap
- **MSIX Packaging** - App packaging with signing and Store readiness
- **Developer Tools** - Manifests, certificates, assets, and build integration

Perfect for:

- **Electron/cross-platform developers** wanting native Windows features or targeting Windows
- **Developers testing and deploying** adding app identity for development or packaging for deployment
- **CI/CD pipelines** automating Windows app builds

## Get started

Checkout our getting started guide for step by step instructions: [Electron guide](https://github.com/microsoft/WinAppCli/blob/main/docs/electron-get-started.md).

## 📋 Usage

Install as a development dependency:

```bash
npm install @microsoft/winappcli --save-dev
```

once installed, call it with npx.

```bash
npx winapp --help
```

### Commands Overview

**Setup Commands:**

- [`init`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#init) - Initialize project with Windows SDK and App SDK
- [`restore`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#restore) - Restore packages and dependencies
- [`update`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#update) - Update packages and dependencies to latest versions

**App Identity & Debugging:**

- [`package`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#package) - Create MSIX packages from directories
- [`create-debug-identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#create-debug-identity) - Add temporary app identity for debugging
- [`manifest`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#manifest) - Generate and manage AppxManifest.xml files

**Certificates & Signing:**

- [`cert`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#cert) - Generate and install development certificates
- [`sign`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#sign) - Sign MSIX packages and executables

**Development Tools:**

- [`tool`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#tool) - Access Windows SDK tools
- [`get-winapp-path`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#get-winapp-path) - Get paths to installed SDK components

**Node.js/Electron Specific:**

- [`node create-addon`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-create-addon) - Generate native C# or C++ addons
- [`node generate-bindings`](#js-bindings-generation) - Generate JS/TS bindings from WinRT metadata
- [`node add-electron-debug-identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#node-add-electron-debug-identity) - Add identity to Electron processes

The full CLI usage can be found here: [Documentation](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md)

### JS Bindings Generation

The CLI can automatically generate typed JavaScript bindings for Windows Runtime APIs using [winrt-meta](https://www.npmjs.com/package/winrt-meta).

**How it works:**
- After `winapp restore` or `winapp init`, the CLI automatically discovers the Windows App SDK AI metadata package from the NuGet dependency chain and generates JS bindings
- Runs automatically if `winrt-meta` is installed in the project — no manual scripts needed
- On first run, adds a default `jsBindings` section to `winapp.yaml`

**Setup:**

`winrt-meta` is included as a dependency — no extra install needed.

```bash
npx winapp restore        # auto-generates bindings after restore
```

**Manual re-generation:**

```bash
npx winapp node generate-bindings [--verbose]
```

**Optional configuration** in `winapp.yaml` for additional Windows SDK types needed by your app:

```yaml
jsBindings:
  lang: js                  # js (default) | cjs | ts
  output: generated-js      # default: generated-js
  systemTypes:
    - namespace: Windows.Storage
      classes: StorageFile
    - namespace: Windows.Graphics.Imaging
      classes: BitmapDecoder
```

The AI metadata package and version are auto-discovered — only `systemTypes` needs manual configuration for app-level dependencies not present in the AI API signatures.

### Programmatic API

The package also exports typed async functions for all CLI commands and utility helpers, so you can use them directly from TypeScript/JavaScript without spawning a CLI process:

```typescript
import { init, packageApp, certGenerate, generateJsBindings } from '@microsoft/winappcli';

await init({ useDefaults: true });
await certGenerate({ install: true });
await packageApp({ inputFolder: './dist', cert: './devcert.pfx' });

// Generate JS bindings from WinRT metadata
await generateJsBindings({ verbose: true });
```

Full programmatic API reference: [NPM API Documentation](https://github.com/microsoft/WinAppCli/blob/main/docs/npm-usage.md)

## 🔧 Feedback

- [File an issue, feature request or bug](https://github.com/microsoft/WinAppCli/issues): please ensure that you are not filing a duplicate issue
- Send feedback to <windowsdevelopertoolkit@microsoft.com>: Do you love this tool? Are there features or fixes you want to see? Let us know!

We are actively working on improving Node and Python support. These features are experimental and we are aware of several issues with these app types.

## 🧾 Samples

[Electron sample](https://github.com/microsoft/WinAppCli/blob/main/samples/electron/README.md): a default Electron Forge generated application + initialized a winapp project with appxmanifest, assets + native addon + C# addon + generates cert

## Support

Need help or have questions about the Windows App Development CLI? Visit our **[Support Guide](https://github.com/microsoft/WinAppCli/blob/main/SUPPORT.md)** for information about our issue templates and triage process.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
