<#
.SYNOPSIS
    Mirrors the GitHub Copilot plugin under .github/plugin/ to a Claude Code
    compatible plugin at .claude/.

.DESCRIPTION
    Claude Code discovers project-scoped subagents at .claude/agents/<name>.md
    and skills at .claude/skills/<skill-name>/SKILL.md. The Copilot plugin in
    this repo (under .github/plugin/) uses an almost-identical layout, so this
    script rebuilds the .claude/ tree from the Copilot source of truth:

    Skills:
      .github/plugin/skills/winapp-cli/<dir>/SKILL.md (+ siblings like references/)
        -> .claude/skills/<frontmatter-name>/SKILL.md (+ copied siblings)

    Agents:
      .github/plugin/agents/<name>.agent.md
        -> .claude/agents/<name>.md  (Copilot-only `infer:` frontmatter stripped)

    Re-run after editing skills/agents under .github/plugin/. The script wipes
    .claude/skills and .claude/agents before regenerating so removed skills
    don't linger.

.PARAMETER Check
    If set, exits with code 1 when .claude/ is out of date instead of writing.
    Useful for CI to enforce that contributors re-ran the sync.
#>

[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Resolve-Path (Join-Path $PSScriptRoot '..')
$copilotRoot  = Join-Path $repoRoot '.github\plugin'
$claudeRoot   = Join-Path $repoRoot '.claude'
$srcSkillsDir = Join-Path $copilotRoot 'skills\winapp-cli'
$srcAgentsDir = Join-Path $copilotRoot 'agents'

if (-not (Test-Path $srcSkillsDir)) { throw "Source skills dir not found: $srcSkillsDir" }
if (-not (Test-Path $srcAgentsDir)) { throw "Source agents dir not found: $srcAgentsDir" }

function Get-FrontmatterName {
    param([string]$Path)
    $inFm = $false
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^---\s*$') {
            if (-not $inFm) { $inFm = $true; continue } else { break }
        }
        if ($inFm -and $line -match '^name:\s*(\S+)') { return $Matches[1] }
    }
    throw "No 'name:' in frontmatter of $Path"
}

function Remove-InferField {
    param([string[]]$Lines)
    $out = New-Object System.Collections.Generic.List[string]
    $inFm = $false; $fmCount = 0
    foreach ($line in $Lines) {
        if ($line -match '^---\s*$') {
            $fmCount++
            $inFm = ($fmCount -eq 1)
            $out.Add($line); continue
        }
        if ($inFm -and $line -match '^infer:\s*') { continue }
        $out.Add($line)
    }
    return ,$out.ToArray()
}

# Build target tree in a temp dir, then compare/swap.
$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-sync-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpRoot | Out-Null
try {
    $tmpSkills = Join-Path $tmpRoot 'skills'
    $tmpAgents = Join-Path $tmpRoot 'agents'
    New-Item -ItemType Directory -Path $tmpSkills, $tmpAgents | Out-Null

    # ---- Skills ----
    foreach ($skillDir in Get-ChildItem -LiteralPath $srcSkillsDir -Directory) {
        $skillFile = Join-Path $skillDir.FullName 'SKILL.md'
        if (-not (Test-Path $skillFile)) { continue }
        $skillName = Get-FrontmatterName -Path $skillFile
        $destDir = Join-Path $tmpSkills $skillName
        Copy-Item -LiteralPath $skillDir.FullName -Destination $destDir -Recurse
        Write-Host "skill : $($skillDir.Name) -> .claude/skills/$skillName"
    }

    # ---- Agents ----
    foreach ($agentFile in Get-ChildItem -LiteralPath $srcAgentsDir -Filter '*.agent.md' -File) {
        $agentName = $agentFile.BaseName -replace '\.agent$',''  # strip trailing .agent
        $cleaned = Remove-InferField -Lines (Get-Content -LiteralPath $agentFile.FullName)
        $destFile = Join-Path $tmpAgents ($agentName + '.md')
        Set-Content -LiteralPath $destFile -Value $cleaned -Encoding utf8 -NoNewline:$false
        Write-Host "agent : $($agentFile.Name) -> .claude/agents/$agentName.md"
    }

    # Compare with current .claude/{skills,agents}
    function Get-TreeHash {
        param([string]$Root)
        if (-not (Test-Path $Root)) { return @{} }
        $map = @{}
        foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File) {
            $rel = $f.FullName.Substring($Root.Length).TrimStart('\','/').Replace('\','/')
            $map[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        }
        return $map
    }

    $newMap = @{}
    foreach ($kv in (Get-TreeHash -Root $tmpRoot).GetEnumerator()) { $newMap[$kv.Key] = $kv.Value }

    $curRoot = $claudeRoot
    $curMap = @{}
    foreach ($sub in 'skills','agents') {
        $p = Join-Path $curRoot $sub
        foreach ($kv in (Get-TreeHash -Root $p).GetEnumerator()) {
            $curMap["$sub/$($kv.Key)"] = $kv.Value
        }
    }

    $diff = $false
    foreach ($k in $newMap.Keys) { if ($curMap[$k] -ne $newMap[$k]) { $diff = $true; break } }
    if (-not $diff) { foreach ($k in $curMap.Keys) { if (-not $newMap.ContainsKey($k)) { $diff = $true; break } } }

    if ($Check) {
        if ($diff) {
            Write-Error ".claude/ is out of date. Run: pwsh scripts/sync-claude-plugin.ps1"
            exit 1
        }
        Write-Host ".claude/ is up to date." -ForegroundColor Green
        return
    }

    if (-not $diff) {
        Write-Host ".claude/ already up to date — no changes." -ForegroundColor Green
        return
    }

    # Wipe and replace skills/ and agents/ under .claude/
    foreach ($sub in 'skills','agents') {
        $p = Join-Path $claudeRoot $sub
        if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    if (-not (Test-Path $claudeRoot)) { New-Item -ItemType Directory -Path $claudeRoot | Out-Null }
    Copy-Item -LiteralPath (Join-Path $tmpRoot 'skills') -Destination (Join-Path $claudeRoot 'skills') -Recurse
    Copy-Item -LiteralPath (Join-Path $tmpRoot 'agents') -Destination (Join-Path $claudeRoot 'agents') -Recurse

    Write-Host "`n.claude/ regenerated from .github/plugin/." -ForegroundColor Green
}
finally {
    if (Test-Path $tmpRoot) { Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
