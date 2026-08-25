#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that plugins/winapp conforms to the Agent Plugins 1.0 specification
.DESCRIPTION
    Checks the portable manifest against the closed Agent Plugins 1.0 schema, verifies
    that portable components sit in their fixed locations, and guards the host-specific
    compatibility files that the spec does not cover.

    The Agent Plugins specification requires clients to validate without retrieving the
    schema, so this script encodes the schema rules rather than fetching them. That also
    keeps CI deterministic and offline-safe.

    Requires no build output and can be run standalone:
        .\scripts\validate-plugin-package.ps1
.PARAMETER PluginRoot
    Path to the plugin package root (default: plugins/winapp)
.PARAMETER FailOnError
    Exit with code 1 when a conformance error is found (default: true)
#>

param(
    [string]$PluginRoot = "",
    [switch]$FailOnError = $true
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent
if (-not $PluginRoot) {
    $PluginRoot = Join-Path $ProjectRoot "plugins\winapp"
}

$SchemaId = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"
$McpSchemaId = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json"
$AllowedFields = @(
    '$schema', 'name', 'version', 'description', 'author',
    'homepage', 'repository', 'license', 'keywords', 'extensions'
)
$CopilotAgent = "com.github.copilot/agents/winapp.agent.md"

$Errors = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$Message) { $script:Errors.Add($Message) }

function Read-JsonFile([string]$Path, [string]$Label) {
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    try {
        return $text | ConvertFrom-Json -Depth 100
    }
    catch {
        Add-Failure "$Label is not valid JSON: $($_.Exception.Message)"
        return $null
    }
}

Write-Host "[VALIDATE] Checking Agent Plugins 1.0 conformance..." -ForegroundColor Blue
Write-Host "Plugin root: $PluginRoot" -ForegroundColor Gray

# --- Portable manifest (spec section 5) -------------------------------------------------
$ManifestPath = Join-Path $PluginRoot "plugin.json"
if (-not (Test-Path $ManifestPath -PathType Leaf)) {
    Add-Failure "portable manifest not found at $ManifestPath"
}
else {
    $manifest = Read-JsonFile $ManifestPath "plugins/winapp/plugin.json"
    if ($manifest) {
        if ($manifest.'$schema' -ne $SchemaId) {
            Add-Failure "plugins/winapp/plugin.json must declare `$schema as $SchemaId"
        }

        $name = $manifest.name
        if ($name -isnot [string] -or [string]::IsNullOrEmpty($name)) {
            Add-Failure "plugins/winapp/plugin.json requires a non-empty string 'name'"
        }
        elseif ($name.Length -gt 64 -or
                $name -cnotmatch '^[a-z0-9]([a-z0-9.\-]*[a-z0-9])?$' -or
                $name.Contains('--') -or $name.Contains('..')) {
            Add-Failure "plugins/winapp/plugin.json name '$name' violates Agent Plugins 1.0 name constraints (1-64 chars, lowercase a-z 0-9 . -, alphanumeric start/end, no '--' or '..')"
        }

        # The schema is closed: any other top-level field is a conformance violation.
        $unknown = @($manifest.PSObject.Properties.Name | Where-Object { $AllowedFields -notcontains $_ })
        if ($unknown.Count -gt 0) {
            Add-Failure "plugins/winapp/plugin.json has non-portable top-level field(s): $($unknown -join ', '). Component paths are auto-discovered; host-specific data belongs under 'extensions'."
        }

        foreach ($field in @('version', 'description', 'homepage', 'repository', 'license')) {
            $value = $manifest.PSObject.Properties[$field]
            if ($value -and $value.Value -isnot [string]) {
                Add-Failure "plugins/winapp/plugin.json '$field' must be a string"
            }
        }

        $author = $manifest.PSObject.Properties['author']
        if ($author) {
            if ($author.Value -isnot [System.Management.Automation.PSCustomObject]) {
                Add-Failure "plugins/winapp/plugin.json 'author' must be an object"
            }
            else {
                $badAuthor = @($author.Value.PSObject.Properties |
                    Where-Object { @('name', 'email', 'url') -notcontains $_.Name -or $_.Value -isnot [string] })
                if ($badAuthor.Count -gt 0) {
                    Add-Failure "plugins/winapp/plugin.json 'author' allows only string 'name', 'email', and 'url'"
                }
            }
        }

        $keywords = $manifest.PSObject.Properties['keywords']
        if ($keywords -and @($keywords.Value | Where-Object { $_ -isnot [string] }).Count -gt 0) {
            Add-Failure "plugins/winapp/plugin.json 'keywords' must be an array of strings"
        }

        $extensions = $manifest.PSObject.Properties['extensions']
        if ($extensions) {
            if ($extensions.Value -isnot [System.Management.Automation.PSCustomObject]) {
                Add-Failure "plugins/winapp/plugin.json 'extensions' must be an object"
            }
            elseif (@($extensions.Value.PSObject.Properties |
                    Where-Object { $_.Value -isnot [System.Management.Automation.PSCustomObject] }).Count -gt 0) {
                Add-Failure "plugins/winapp/plugin.json 'extensions' must map each namespace to an object"
            }
        }
    }
}

# --- Portable skills, fixed location (spec sections 6.1 and 7.1) ------------------------
$SkillsRoot = Join-Path $PluginRoot "skills"
if (-not (Test-Path $SkillsRoot -PathType Container)) {
    Add-Failure "portable skills directory not found at $SkillsRoot"
}
else {
    $skillDirs = @(Get-ChildItem $SkillsRoot -Directory)
    if ($skillDirs.Count -eq 0) {
        Add-Failure "no skill directories found under $SkillsRoot"
    }

    foreach ($skillDir in $skillDirs) {
        $skillFile = Join-Path $skillDir.FullName "SKILL.md"
        if (-not (Test-Path $skillFile -PathType Leaf)) {
            Add-Failure "skill directory lacks SKILL.md: plugins/winapp/skills/$($skillDir.Name)"
            continue
        }

        # Agent Skills requires YAML frontmatter carrying name and description.
        $lines = [System.IO.File]::ReadAllLines($skillFile, [System.Text.UTF8Encoding]::new($false))
        if ($lines.Count -eq 0 -or $lines[0].Trim() -ne '---') {
            Add-Failure "skills/$($skillDir.Name)/SKILL.md is missing YAML frontmatter (first line must be '---')"
            continue
        }

        $closing = -1
        for ($i = 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '---') { $closing = $i; break }
        }
        if ($closing -lt 0) {
            Add-Failure "skills/$($skillDir.Name)/SKILL.md has an unterminated YAML frontmatter block"
            continue
        }

        $front = $lines[1..($closing - 1)]
        foreach ($key in @('name', 'description')) {
            if (@($front | Where-Object { $_ -match "^$key\s*:\s*\S" }).Count -eq 0) {
                Add-Failure "skills/$($skillDir.Name)/SKILL.md frontmatter is missing required '$key'"
            }
        }
    }

    # Clients do not recurse past immediate children, so a deeper SKILL.md never loads.
    foreach ($stray in Get-ChildItem $SkillsRoot -Recurse -File -Filter "SKILL.md") {
        if ($stray.Directory.Parent.FullName -ne (Resolve-Path $SkillsRoot).Path) {
            $relative = $stray.FullName.Substring($PluginRoot.Length).TrimStart('\', '/')
            Add-Failure "SKILL.md at $relative is nested too deep to be discovered; skills must be immediate children of skills/"
        }
    }
}

# --- MCP configuration, only validated when present (spec section 7.2) ------------------
$McpPath = Join-Path $PluginRoot "mcp.json"
if (Test-Path $McpPath -PathType Leaf) {
    $mcp = Read-JsonFile $McpPath "plugins/winapp/mcp.json"
    if ($mcp) {
        if ($mcp.'$schema' -ne $McpSchemaId) {
            Add-Failure "plugins/winapp/mcp.json must declare `$schema as $McpSchemaId"
        }
        if (-not $mcp.PSObject.Properties['mcpServers']) {
            Add-Failure "plugins/winapp/mcp.json must contain 'mcpServers'"
        }
        $extraMcp = @($mcp.PSObject.Properties.Name | Where-Object { @('$schema', 'mcpServers') -notcontains $_ })
        if ($extraMcp.Count -gt 0) {
            Add-Failure "plugins/winapp/mcp.json has unsupported top-level field(s): $($extraMcp -join ', ')"
        }
    }
}

# --- Copilot extension namespace (spec section 8.2) -------------------------------------
if (-not (Test-Path (Join-Path $PluginRoot $CopilotAgent) -PathType Leaf)) {
    Add-Failure "Copilot agent not found at plugins/winapp/$CopilotAgent. Copilot-specific components must live under the com.github.copilot/ namespace."
}

# --- Claude Code compatibility ----------------------------------------------------------
# Claude is not an Agent Plugins client. It keeps its own manifest, and its 'agents' field
# points at the Copilot-namespaced file so the agent is not duplicated. A rename that
# breaks that pointer would otherwise fail silently for Claude users only.
$ClaudeManifestPath = Join-Path $PluginRoot ".claude-plugin\plugin.json"
if (-not (Test-Path $ClaudeManifestPath -PathType Leaf)) {
    Add-Failure "Claude Code manifest not found at $ClaudeManifestPath"
}
else {
    $claude = Read-JsonFile $ClaudeManifestPath "plugins/winapp/.claude-plugin/plugin.json"
    if ($claude) {
        $claudeAgents = @($claude.agents)
        if ($claudeAgents.Count -eq 0) {
            Add-Failure "plugins/winapp/.claude-plugin/plugin.json must declare 'agents' pointing at the Copilot-namespaced agent (Claude does not read com.github.copilot/ by default)"
        }
        foreach ($agentRef in $claudeAgents) {
            $resolved = Join-Path $PluginRoot ($agentRef -replace '^\./', '' -replace '/', '\')
            if (-not (Test-Path $resolved -PathType Leaf)) {
                Add-Failure "plugins/winapp/.claude-plugin/plugin.json 'agents' entry '$agentRef' does not resolve to a file"
            }
        }
    }
}

# --- Repo-root legacy shim --------------------------------------------------------------
# The root manifest intentionally stays in the legacy Copilot format. Declaring $schema
# there would opt it into the closed schema, demoting its nested skills/agents paths to
# ignored unknown fields and breaking `copilot plugin install microsoft/WinAppCli`.
$RootManifestPath = Join-Path $ProjectRoot "plugin.json"
if (-not (Test-Path $RootManifestPath -PathType Leaf)) {
    Add-Failure "repo-root plugin.json shim not found"
}
else {
    $root = Read-JsonFile $RootManifestPath "plugin.json"
    if ($root) {
        if ($root.PSObject.Properties['$schema']) {
            Add-Failure "repo-root plugin.json must NOT declare `$schema. It is the legacy Copilot shim; adding `$schema turns its nested 'skills'/'agents' paths into ignored unknown fields and breaks 'copilot plugin install microsoft/WinAppCli'."
        }
        foreach ($field in @('agents', 'skills')) {
            foreach ($pathRef in @($root.$field)) {
                if (-not $pathRef) { continue }
                $resolved = Join-Path $ProjectRoot ($pathRef -replace '/', '\')
                if (-not (Test-Path $resolved)) {
                    Add-Failure "repo-root plugin.json '$field' path '$pathRef' does not exist"
                }
            }
        }
    }
}

# --- Report ------------------------------------------------------------------------------
if ($Errors.Count -gt 0) {
    foreach ($failure in $Errors) {
        Write-Host "::error::$failure" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "See https://agent-plugins.org/specification for the Agent Plugins 1.0 rules." -ForegroundColor Yellow
    if ($FailOnError) {
        exit 1
    }
    exit 0
}

Write-Host "[VALIDATE] plugins/winapp conforms to Agent Plugins 1.0" -ForegroundColor Green
exit 0
