# Packaging an MCP Server as MSIX

This guide walks you through converting an [MCP Bundle](https://github.com/modelcontextprotocol/mcpb) (`.mcpb`) to a signed MSIX package with Windows ODR (On-Device Registration) and containment support.

## Why MSIX for MCP Servers?

MCP Bundles are easy to create and cross-platform, but on Windows they lack **package identity** — which means:

- No containment (sandbox isolation) in agent sessions
- No enterprise management via Intune/Group Policy
- No verifiable publisher identity

Converting to MSIX gives your MCP server full Windows platform integration while keeping MCPB's authoring simplicity.

## Prerequisites

- An MCP Bundle (`.mcpb`) file with a valid `manifest.json`
- The manifest must include `_meta.com.microsoft.windows.static_responses` (required for ODR)

## Steps

### 1. Install winapp CLI

```
winget install Microsoft.winappcli --source winget
```

### 2. Convert .mcpb to .msix

```bash
# Basic conversion with auto-generated test certificate
winapp package --mcpb ./my-server.mcpb --generate-cert

# Specify architecture (default: x64)
winapp package --mcpb ./my-server.mcpb --generate-cert --architecture arm64

# Use an existing certificate
winapp package --mcpb ./my-server.mcpb --cert ./mycert.pfx --cert-password "mypass"

# Custom output path and publisher
winapp package --mcpb ./my-server.mcpb --generate-cert --output MyServer.msix --publisher "CN=My Company"
```

### 3. Install for Testing

```powershell
# Trust the development certificate (one-time, requires admin)
winapp cert install ./devcert.pfx

# Install the MSIX
Add-AppxPackage -Path ./my-server.msix
```

### 4. Verify Registration

```
odr mcp list
```

Your server should appear in the list of registered MCP servers.

## What the Converter Does

1. **Extracts** the `.mcpb` ZIP archive
2. **Validates** `manifest.json` — checks required fields, `static_responses`, and entry point
3. **Generates** `AppxManifest.xml` with:
   - `uap3:AppExtension` for `com.microsoft.windows.ai.mcpServer` registration
   - `uap5:ExecutionAlias` for the server binary
   - `TrustedLaunch` declaration (required for containment)
4. **Stages** server files, icons, and MCP manifest
5. **Packages** with MakeAppx and signs with SignTool

## MCPB Manifest Requirements

Your `manifest.json` must include:

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Server name (used as package identity) |
| `version` | Yes | Semver version (auto-converted to 4-part) |
| `description` | Yes | Server description |
| `server.entry_point` | Yes | Path to the server executable |
| `server.type` | No | `binary` (default) or runtime type (`node`, `python`) |
| `_meta.com.microsoft.windows.static_responses` | Yes | Must include `initialize` and `tools/list` responses |

### Script-Based Servers

For non-binary servers (Node.js, Python), use `--runtime-path`:

```bash
winapp package --mcpb ./node-server.mcpb --generate-cert --runtime-path "C:\Program Files\nodejs\node.exe"
```

The runtime will be auto-detected from PATH if not specified.

## TrustedLaunch and Self-Signed Certificates

MSIX packages with TrustedLaunch require special handling for self-signed certificates during development. For containment launch (`odr mcp run --proxy`), install via **Windows Device Portal** instead of `Add-AppxPackage`.

See the [winappCli documentation](../usage.md) for more details on certificate management.
