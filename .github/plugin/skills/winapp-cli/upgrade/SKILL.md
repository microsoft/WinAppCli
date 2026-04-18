---
name: winapp-upgrade
description: Update the winapp CLI to the latest version. Use when the user wants to upgrade winapp, check for updates, or troubleshoot version-related issues.
version: 0.2.2
---
## When to use

Use this skill when:
- **Updating the winapp CLI** to the latest available version
- **Checking which version** of winapp is currently installed
- **Troubleshooting version-related issues** where upgrading may resolve the problem

## How it works

The `upgrade` command auto-detects how winapp was installed and upgrades accordingly:

| Install channel | Behavior |
|----------------|----------|
| **MSIX** | Downloads and installs the latest MSIX package |
| **Standalone exe** | Downloads and swaps the executable in-place |
| **npm** | Shows instructions to run `npm update @microsoft/winappcli` |
| **NuGet** | Shows instructions to update the NuGet package in the project |

## Usage

```powershell
# Upgrade to the latest version
winapp upgrade
```

## Troubleshooting

- **"winapp was installed via npm"**: Run `npm update @microsoft/winappcli -g` (or without `-g` for local installs) instead.
- **"winapp was installed via NuGet"**: Update the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package in your project's .csproj.
- **Permission errors on MSIX upgrade**: Ensure you have permission to install MSIX packages on the machine. The MSIX package is per-user and does not require admin rights.


## Command Reference

### `winapp upgrade`

Check for and install the latest version of the winapp CLI. For MSIX installs, downloads and installs the latest MSIX. For standalone exe installs, downloads and swaps the executable. For npm or NuGet installs, shows instructions for using the package manager.
