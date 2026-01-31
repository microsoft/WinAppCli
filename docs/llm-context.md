---
name: winapp-cli
description: CLI for generating and managing appxmanifest.xml, image assets, test certificates, Windows (App) SDK projections, package identity, and packaging. For use with any app framework targeting Windows
version: 0.1.11
schema_version: 1.0
---

# winapp CLI Context for LLMs

> Auto-generated from CLI v0.1.11 (schema version 1.0)
> 
> This file provides structured context about the winapp CLI for AI assistants and LLMs.
> For the raw JSON schema, see [cli-schema.json](cli-schema.json).

## Overview

CLI for generating and managing appxmanifest.xml, image assets, test certificates, Windows (App) SDK projections, package identity, and packaging. For use with any app framework targeting Windows

**Installation:**
- WinGet: `winget install Microsoft.WinAppCli --source winget`
- npm: `npm install -g @microsoft/winappcli` (for electron projects)

## Command Reference

### `winapp cert`

Manage development certificates for code signing. Use 'cert generate' to create a self-signed certificate for testing, or 'cert install' (requires elevation) to trust an existing certificate on this machine.

#### `winapp cert generate`

Create a self-signed certificate for local testing only. Publisher must match AppxManifest.xml (auto-inferred if --manifest provided or appxmanifest.xml is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.

**Options:**
- `--if-exists` - Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) (default: `Error`)
- `--install` - Install the certificate to the local machine store after generation
- `--manifest` - Path to appxmanifest.xml file to extract publisher information from
- `--output` - Output path for the generated PFX file
- `--password` - Password for the generated PFX file (default: `password`)
- `--publisher` - Publisher name for the generated certificate. If not specified, will be inferred from manifest.
- `--quiet` / `-q` - Suppress progress messages
- `--valid-days` - Number of days the certificate is valid (default: `365`)
- `--verbose` / `-v` - Enable verbose output

#### `winapp cert install`

Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.

**Arguments:**
- `<cert-path>` *(required)* - Path to the certificate file (PFX or CER)

**Options:**
- `--force` - Force installation even if the certificate already exists
- `--password` - Password for the PFX file (default: `password`)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp create-debug-identity`

Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires appxmanifest.xml in current directory or passed via --manifest. Re-run after changing appxmanifest.xml or Assets/.

**Arguments:**
- `<entrypoint>` - Path to the .exe that will need to run with identity, or entrypoint script.

**Options:**
- `--manifest` - Path to the appxmanifest.xml
- `--no-install` - Do not install the package after creation.
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp get-winapp-path`

Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.

**Options:**
- `--global` - Get the global .winapp directory instead of local
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp init`

Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates appxmanifest.xml with default assets, generates devcert.pfx for code signing, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' or 'cert generate' if you only need one of those pieces.

**Arguments:**
- `<base-directory>` - Base/root directory for the winapp workspace, for consumption or installation.

**Options:**
- `--config-dir` - Directory to read/store configuration (default: current directory)
- `--config-only` - Only handle configuration file operations (create if missing, validate if exists). Skip package installation, certificate generation, and other workspace setup steps.
- `--ignore-config` / `--no-config` - Don't use configuration file for version management
- `--no-cert` - Skip development certificate generation
- `--no-gitignore` - Don't update .gitignore file
- `--quiet` / `-q` - Suppress progress messages
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--use-defaults` / `--no-prompt` - Do not prompt, and use default of all prompts
- `--verbose` / `-v` - Enable verbose output
### `winapp manifest`

Create and modify appxmanifest.xml files for package identity and MSIX packaging. Use 'manifest generate' to create a new manifest, or 'manifest update-assets' to regenerate app icons from a source image.

#### `winapp manifest generate`

Create appxmanifest.xml without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs), 'hostedapp' (Python/Node scripts).

**Arguments:**
- `<directory>` - Directory to generate manifest in

**Options:**
- `--description` - Human-readable app description shown during installation and in Windows Settings (default: `My Application`)
- `--entrypoint` / `--executable` - Entry point of the application (e.g., executable path / name, or .py/.js script if template is HostedApp). Default: <package-name>.exe
- `--if-exists` - Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) (default: `Error`)
- `--logo-path` - Path to logo image file
- `--package-name` - Package name (default: folder name)
- `--publisher-name` - Publisher CN (default: CN=<current user>)
- `--quiet` / `-q` - Suppress progress messages
- `--template` - Manifest template type: 'packaged' (full MSIX app, default), 'sparse' (desktop app with package identity for Windows APIs), or 'hostedapp' (script running under Python/Node host) (default: `Packaged`)
- `--verbose` / `-v` - Enable verbose output
- `--version` - App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). (default: `1.0.0.0`)

#### `winapp manifest update-assets`

Generate new assets for images referenced in an appxmanifest.xml from a single source image. Source image should be at least 400x400 pixels.

**Arguments:**
- `<image-path>` *(required)* - Path to source image file

**Options:**
- `--manifest` - Path to AppxManifest.xml file (default: search current directory)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp package`

Create MSIX installer from your built app. Run after building your app. appxmanifest.xml is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest appxmanifest.xml --cert ./devcert.pfx

**Aliases:** `pack`

**Arguments:**
- `<input-folder>` *(required)* - Input folder with package layout

**Options:**
- `--cert` - Path to signing certificate (will auto-sign if provided)
- `--cert-password` - Certificate password (default: password) (default: `password`)
- `--generate-cert` - Generate a new development certificate
- `--install-cert` - Install certificate to machine
- `--manifest` - Path to AppX manifest file (default: auto-detect from input folder or current directory)
- `--name` - Package name (default: from manifest)
- `--output` - Output msix file name for the generated package (defaults to <name>.msix)
- `--publisher` - Publisher name for certificate generation
- `--quiet` / `-q` - Suppress progress messages
- `--self-contained` - Bundle Windows App SDK runtime for self-contained deployment
- `--skip-pri` - Skip PRI file generation
- `--verbose` / `-v` - Enable verbose output
### `winapp restore`

Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages from existing winapp.yaml without changing versions. Requires winapp.yaml (created by 'init'). To check for newer SDK versions, use 'update' instead.

**Arguments:**
- `<base-directory>` - Base/root directory for the winapp workspace

**Options:**
- `--config-dir` - Directory to read configuration from (default: current directory)
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp sign`

Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.

**Arguments:**
- `<file-path>` *(required)* - Path to the file/package to sign
- `<cert-path>` *(required)* - Path to the certificate file (PFX format)

**Options:**
- `--password` - Certificate password (default: `password`)
- `--quiet` / `-q` - Suppress progress messages
- `--timestamp` - Timestamp server URL
- `--verbose` / `-v` - Enable verbose output
### `winapp tool`

Run Windows SDK tools directly (makeappx, signtool, makepri, etc.). Auto-downloads Build Tools if needed. For most tasks, prefer higher-level commands like 'package' or 'sign'. Example: winapp tool makeappx pack /d ./folder /p ./out.msix

**Aliases:** `run-buildtool`

**Options:**
- `--quiet` / `-q` - Suppress progress messages
- `--verbose` / `-v` - Enable verbose output
### `winapp update`

Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.

**Options:**
- `--quiet` / `-q` - Suppress progress messages
- `--setup-sdks` - SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)
- `--verbose` / `-v` - Enable verbose output

## Common Workflows

### New Project Setup
1. `winapp init .` - Initialize workspace with appxmanifest.xml, image assets, test certificate, and optionally SDK projections in the .winapp folder. (run with `--use-defaults` to make it non-interactive)
2. Edit `appxmanifest.xml` if you need to modify properties, set capabilities, or other configurations
3. Build your app
4. `winapp create-debug-identity <exe-path>` - to generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
5. Run the app
6. `winapp pack <output-folder-to-package> --cert .\devcert.pfx` - Create signed MSIX (--cert is optional)

### Existing Project (Clone/CI)
1. `winapp restore` - Reinstall packages and generate C++ projections from `winapp.yaml`
2. Build and package as normal

### Update SDK Versions
1. `winapp update` - Check for and install newer SDK versions
2. Rebuild your app

### Install SDKs After Initial Setup
If you ran `init` with `--setup-sdks none` (or skipped SDK installation) and later need the SDKs:
1. `winapp init --use-defaults --setup-sdks stable` - Re-run init to install SDKs
   - `--use-defaults` skips prompts and preserves existing files (manifest, cert, etc.)
   - Use `--setup-sdks preview` or `--setup-sdks experimental` for preview/experimental SDK versions
2. Rebuild your app with the new SDK projections in `.winapp/`

### Debug with Package Identity
For apps that need Windows APIs requiring identity (push notifications, etc.):
1. Ensure an appxmanifest.xml is present, either via `winapp init` or `winapp manifest generate`
2. `winapp create-debug-identity ./myapp.exe` - generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
3. Run your app - it now has package identity

### Electron Apps
1. `winapp init` - Set up workspace (run with --use-defaults to make it non-interactive)
2. `winapp node create-addon --template cs` - Generate native C# addon for Windows APIs (`--template cpp` for C++ addon)
3. `winapp node add-electron-debug-identity` - Enable identity for debugging
4. `npm start` to launch app normally, but now with identity
5. For production, create production files with the preferred packager and run `winapp pack <generated-production-files> --cert .\devcert.pfx`

## Command Selection Guide

Use this decision tree to pick the right command:

```
Using winapp CLI in a new project?
├─ Yes → run winapp init
│        (creates manifest + cert + SDKs + config)
└─ No
   ├─ Cloned/pulled a repo with winapp.yaml?
   │  └─ Yes → winapp restore
   │           (reinstalls SDKs from config)
   ├─ Want newer SDK versions?
   │  └─ Yes → winapp update
   │           (checks NuGet, updates config)
   ├─ Only need a manifest (no SDKs/cert)?
   │  └─ Yes → winapp manifest generate
   ├─ Only need a dev certificate?
   │  └─ Yes → winapp cert generate
   ├─ Ready to create MSIX installer?
   │  └─ Yes → winapp package <build-output>
   │           (add --cert for signing)
   ├─ Need to add package identity for debugging Windows APIs that need it?
   │  └─ Yes → winapp create-debug-identity <exe>
   │           (enables push notifications, etc.)
   └─ Need to run SDK tools directly?
      └─ Yes → winapp tool <toolname> <args>
               (makeappx, signtool, makepri)
```

## Prerequisites & State

| Command | Requires | Creates/Modifies |
|---------|----------|------------------|
| `init` | Nothing | `winapp.yaml`, `.winapp/`, `appxmanifest.xml`, `Assets/`, `devcert.pfx` |
| `restore` | `winapp.yaml` | `.winapp/packages/` |
| `update` | `winapp.yaml` | Updates versions in `winapp.yaml` |
| `manifest generate` | Nothing | `appxmanifest.xml`, `Assets/` |
| `cert generate` | Nothing (or `appxmanifest.xml` for publisher inference) | `*.pfx` file |
| `package` | App build output + `appxmanifest.xml` (+ `devcert.pfx` for optional signing) | `*.msix` file |
| `create-debug-identity` | `appxmanifest.xml` + exe | Registers sparse package with Windows |

## Common Errors & Solutions

| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore` or `update` without config | Run `winapp init` first, or ensure you're in the right directory |
| "appxmanifest.xml not found" | Running `package` or `create-debug-identity` without manifest | Run `winapp init` or `winapp manifest generate` first |
| "Publisher mismatch" | Certificate publisher doesn't match manifest | Regenerate cert with `--manifest` flag, or edit manifest Publisher to match |
| "Access denied" / "elevation required" | `cert install` without admin | Run terminal as Administrator |
| "Package installation failed" | Signing issue or existing package conflict | Run `Get-AppxPackage <name> | Remove-AppxPackage` first, ensure cert is trusted |
| "Certificate not trusted" | Dev cert not installed on machine | Run `winapp cert install ./devcert.pfx` as admin |
| "Build tools not found" | First run, tools not downloaded yet | winapp auto-downloads; ensure internet access. Or run `winapp tool --help` to trigger download |

## Machine-Readable Schema

For programmatic access to the complete CLI structure including all options, types, and defaults:

```bash
winapp --cli-schema
```

This outputs JSON that can be parsed by tools and LLMs. See [cli-schema.json](cli-schema.json).

