# MCP Server Sample

This sample demonstrates how to package an MCP server as an MSIX using the `winapp` CLI.

## Overview

A minimal C# MCP server that provides two tools:
- `get_greeting` — Returns a greeting for a given name
- `get_time` — Returns the current UTC time

## Prerequisites

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- [winapp CLI](../../README.md#-installation)

## Quick Start

### 1. Build the MCP server

```bash
cd SampleMcpServer
dotnet publish -c Release -r win-x64 --self-contained
```

### 2. Create the .mcpb bundle

The `.mcpb` is a ZIP file containing the published output and `manifest.json`:

```powershell
# Copy manifest.json to the publish output
$publishDir = "SampleMcpServer\bin\Release\net8.0\win-x64\publish"
Copy-Item manifest.json $publishDir

# Create the .mcpb (ZIP)
Compress-Archive -Path "$publishDir\*" -DestinationPath sample-server.mcpb -Force
```

### 3. Convert to MSIX

```bash
winapp pack --mcpb ./sample-server.mcpb --generate-cert --install-cert
```

### 4. Install and verify

```powershell
Add-AppxPackage -Path .\SampleMcpServer.msix
odr mcp list  # Should show SampleMcpServer
```

## Files

| File | Description |
|------|-------------|
| `SampleMcpServer/` | C# MCP server project |
| `manifest.json` | MCPB manifest with `_meta.com.microsoft.windows` section |
