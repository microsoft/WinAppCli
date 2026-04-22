# Using WinApp with Claude Code

The winapp agent plugin is available for [Claude Code](https://code.claude.com/) via the plugin marketplace system. Once installed, Claude Code gains expert knowledge of the winapp CLI and can guide you through Windows app packaging, signing, identity, SDK setup, and UI automation.

## Installation

### 1. Add the WinAppCli marketplace

Open Claude Code and run:

```
/plugin marketplace add microsoft/WinAppCli
```

Or use the interactive plugin manager:
1. Type `/plugin` to open the plugin manager
2. Navigate to the **Marketplaces** tab
3. Select **Add marketplace**
4. Enter: `microsoft/WinAppCli`

### 2. Install the winapp plugin

```
/plugin install winapp@WinAppCli
```

Or use the interactive plugin manager:
1. Type `/plugin` to open the plugin manager
2. Navigate to the **Discover** tab
3. Find and install **winapp**

### 3. Verify installation

The winapp agent and skills should now be available. You can verify by checking:
1. `/plugin` → **Installed** tab → **winapp** should appear
2. Ask Claude Code about packaging a Windows app — it should activate the winapp agent automatically

## What's included

The winapp plugin provides:

- **Agent**: An expert Windows app development agent that activates automatically when you ask about MSIX packaging, code signing, Windows SDK setup, package identity, manifest authoring, or UI automation
- **Skills**: Detailed guidance for setup, packaging, signing, identity, manifests, troubleshooting, framework-specific workflows, and UI automation

## Updating

To update to the latest version:

```
/plugin marketplace update WinAppCli
/reload-plugins
```

## Supported frameworks

The winapp plugin covers all major app frameworks:
- **Electron** — native addons, debug identity, MSIX packaging
- **.NET** (WPF, WinForms, Console) — direct Windows API access, packaging
- **C++** — CppWinRT projections, SDK management
- **Rust** — windows-rs integration, packaging
- **Flutter** — Windows build + MSIX packaging
- **Tauri** — MSIX packaging alongside Tauri's own bundler

## Prerequisites

The winapp CLI must be installed on your machine for the agent to run commands:

```powershell
# Via winget (recommended for non-Node projects)
winget install Microsoft.WinAppCli --source winget

# Via npm (recommended for Electron/Node projects)
npm install --save-dev @microsoft/winappcli
```

## Troubleshooting

### Plugin not showing up after install
Run `/reload-plugins` to refresh the plugin list.

### Marketplace add fails
Ensure you have access to the `microsoft/WinAppCli` GitHub repository. For private repo access, set `GITHUB_TOKEN` in your environment.

### Agent not activating
The winapp agent activates automatically when your prompt involves Windows app development topics (MSIX, packaging, signing, manifests, Windows SDK, etc.). You can also invoke it explicitly via the `/agents` interface.
