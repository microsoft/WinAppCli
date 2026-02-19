#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate AI-powered release notes from merged pull requests
.DESCRIPTION
    This script generates categorized release notes by:
    1. Detecting the tag range (previous release to current)
    2. Fetching a structured PR list via the GitHub generate-notes API
    3. Enriching with PR body details via the GitHub REST API
    4. Calling the GitHub Models API (GPT-4o-mini) to produce polished prose
    Falls back to raw GitHub-generated notes if the AI call fails.
.PARAMETER PreviousTag
    Previous release tag (auto-detected from git tags if empty)
.PARAMETER CurrentTag
    Current release tag (auto-detected from version.json if empty)
.PARAMETER CurrentRef
    Git ref for the current release commit (default: HEAD)
.PARAMETER GitHubToken
    GitHub token for API calls (falls back to GH_TOKEN or GITHUB_TOKEN env vars)
.PARAMETER RepoOwner
    GitHub repository owner (default: microsoft)
.PARAMETER RepoName
    GitHub repository name (default: winappcli)
.PARAMETER OutputPath
    Path to write the release notes markdown (default: artifacts/release-notes.md)
.PARAMETER ModelsToken
    Separate token for GitHub Models API (falls back to GH_MODELS_TOKEN env var, then GitHubToken)
.PARAMETER Model
    GitHub Models model to use (default: openai/gpt-4o-mini)
.PARAMETER SkipAI
    Skip the AI summarization step and output raw GitHub-generated notes
.PARAMETER DebugLog
    Write debug log with all retrieved PR data and prompts sent to the LLM (saved next to OutputPath)
.EXAMPLE
    .\scripts\generate-release-notes.ps1
.EXAMPLE
    .\scripts\generate-release-notes.ps1 -PreviousTag "v0.1.10" -SkipAI
.EXAMPLE
    $env:GH_TOKEN = "ghp_..."; .\scripts\generate-release-notes.ps1 -PreviousTag "v0.1.10"
#>

param(
    [string]$PreviousTag = "",
    [string]$CurrentTag = "",
    [string]$CurrentRef = "HEAD",
    [string]$GitHubToken = "",
    [string]$ModelsToken = "",
    [string]$RepoOwner = "microsoft",
    [string]$RepoName = "winappcli",
    [string]$OutputPath = "",
    [string]$Model = "openai/gpt-4o-mini",
    [switch]$SkipAI = $false,
    [switch]$DebugLog = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent

# --- Resolve GitHub token ---
if (-not $GitHubToken) {
    $GitHubToken = if ($env:GH_TOKEN) { $env:GH_TOKEN }
                   elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN }
                   else { "" }
}

if (-not $GitHubToken) {
    Write-Warning "[RELEASENOTES] No GitHub token found. Set GH_TOKEN or pass -GitHubToken."
    Write-Warning "[RELEASENOTES] Falling back to git-log-only mode."
}

# --- Resolve Models token (separate token for GitHub Models API, falls back to GitHubToken) ---
if (-not $ModelsToken) {
    $ModelsToken = if ($env:GH_MODELS_TOKEN) { $env:GH_MODELS_TOKEN }
                   else { $GitHubToken }
}

# --- Resolve output path ---
if (-not $OutputPath) {
    $OutputPath = Join-Path $ProjectRoot "artifacts\release-notes.md"
}

$outputDir = Split-Path $OutputPath -Parent
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# --- Debug log setup ---
$debugLogPath = Join-Path $outputDir "release-notes-debug.log"
function Write-DebugLog {
    param([string]$Label, [string]$Content)
    if (-not $DebugLog) { return }
    $separator = "`n$('=' * 80)`n"
    $entry = "$separator[$Label] $(Get-Date -Format 'HH:mm:ss')$separator$Content`n"
    $entry | Out-File -FilePath $debugLogPath -Append -Encoding UTF8
    Write-Host "[RELEASENOTES][DEBUG] $Label written to $debugLogPath" -ForegroundColor Magenta
}

if ($DebugLog) {
    "" | Set-Content -Path $debugLogPath -Encoding UTF8 -NoNewline
    Write-Host "[RELEASENOTES] Debug logging enabled -> $debugLogPath" -ForegroundColor Magenta
}

# --- Helper: GitHub API request ---
function Invoke-GitHubApi {
    param(
        [string]$Endpoint,
        [string]$Method = "GET",
        [object]$Body = $null
    )

    $headers = @{
        "Accept"     = "application/vnd.github.v3+json"
        "User-Agent" = "WinAppCLI-ReleaseNotes"
    }
    if ($GitHubToken) {
        $headers["Authorization"] = "token $GitHubToken"
    }

    $uri = "https://api.github.com$Endpoint"
    $params = @{
        Uri     = $uri
        Method  = $Method
        Headers = $headers
    }

    if ($Body) {
        $params["Body"] = ($Body | ConvertTo-Json -Depth 10)
        $params["ContentType"] = "application/json"
    }

    return Invoke-RestMethod @params
}

# --- Step 1: Determine tag range ---
Write-Host "[RELEASENOTES] Determining tag range..." -ForegroundColor Cyan

if (-not $CurrentTag) {
    $versionJson = Get-Content (Join-Path $ProjectRoot "version.json") | ConvertFrom-Json
    $CurrentTag = "v$($versionJson.version)"
}

if (-not $PreviousTag) {
    # Find the most recent stable tag that isn't the current version
    $allTags = git tag --sort=-version:refname 2>$null
    $PreviousTag = $allTags | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and $_ -ne $CurrentTag } | Select-Object -First 1

    if (-not $PreviousTag) {
        Write-Warning "[RELEASENOTES] Could not auto-detect previous tag. Using first commit."
        $PreviousTag = git rev-list --max-parents=0 HEAD 2>$null | Select-Object -First 1
    }
}

Write-Host "[RELEASENOTES] Tag range: $PreviousTag -> $CurrentTag ($CurrentRef)" -ForegroundColor Green

# --- Step 2: Get GitHub-generated notes (structured by .github/release.yml) ---
$rawNotes = ""
if ($GitHubToken) {
    Write-Host "[RELEASENOTES] Fetching GitHub-generated release notes..." -ForegroundColor Cyan
    try {
        $generateBody = @{
            tag_name          = $CurrentTag
            previous_tag_name = $PreviousTag
            target_commitish  = $CurrentRef
        }
        $result = Invoke-GitHubApi -Endpoint "/repos/$RepoOwner/$RepoName/releases/generate-notes" -Method "POST" -Body $generateBody
        $rawNotes = $result.body
        Write-Host "[RELEASENOTES] Got GitHub-generated notes ($($rawNotes.Length) chars)" -ForegroundColor Green
    }
    catch {
        Write-Warning "[RELEASENOTES] Failed to fetch GitHub-generated notes: $_"
    }
}

# Fallback: build raw notes from git log
if (-not $rawNotes) {
    Write-Host "[RELEASENOTES] Building notes from git log..." -ForegroundColor Yellow
    $commits = git log "$PreviousTag..$CurrentRef" --oneline 2>$null
    $rawNotes = "## What's Changed`n`n"
    foreach ($commit in $commits) {
        $rawNotes += "* $commit`n"
    }
}

Write-DebugLog -Label "RAW NOTES" -Content $rawNotes

# --- Step 3: Fetch PR bodies for additional context ---
$prDetails = @()
if ($GitHubToken) {
    Write-Host "[RELEASENOTES] Fetching PR details for context..." -ForegroundColor Cyan

    # Extract PR numbers from the raw notes or git log
    $prNumbers = @()
    $allText = $rawNotes + "`n" + (git log "$PreviousTag..$CurrentRef" --oneline 2>$null | Out-String)
    $prMatches = [regex]::Matches($allText, '#(\d+)')
    foreach ($m in $prMatches) {
        $prNumbers += $m.Groups[1].Value
    }
    $prNumbers = $prNumbers | Sort-Object -Unique

    foreach ($prNum in $prNumbers) {
        try {
            $pr = Invoke-GitHubApi -Endpoint "/repos/$RepoOwner/$RepoName/pulls/$prNum"
            $body = if ($pr.body -and $pr.body.Length -gt 2000) { $pr.body.Substring(0, 2000) + "..." } elseif ($pr.body) { $pr.body } else { "" }

            # Fetch Copilot review summary if available (first review contains the overview)
            $copilotReview = ""
            try {
                $reviews = Invoke-GitHubApi -Endpoint "/repos/$RepoOwner/$RepoName/pulls/$prNum/reviews"
                $copilotEntry = $reviews | Where-Object { $_.user.login -eq "copilot-pull-request-reviewer[bot]" } | Select-Object -First 1
                if ($copilotEntry -and $copilotEntry.body) {
                    # Extract just the overview and changes list, skip the file-level details
                    $reviewBody = $copilotEntry.body
                    $detailsIdx = $reviewBody.IndexOf("<details>")
                    if ($detailsIdx -gt 0) {
                        $reviewBody = $reviewBody.Substring(0, $detailsIdx).Trim()
                    }
                    if ($reviewBody.Length -gt 1500) {
                        $reviewBody = $reviewBody.Substring(0, 1500) + "..."
                    }
                    $copilotReview = $reviewBody
                }
            }
            catch {
                # Copilot review not available — not critical
            }

            $prDetails += [PSCustomObject]@{
                Number        = $pr.number
                Title         = $pr.title
                Body          = $body
                Author        = $pr.user.login
                Labels        = ($pr.labels | ForEach-Object { $_.name }) -join ", "
                CopilotReview = $copilotReview
            }
        }
        catch {
            Write-Warning "[RELEASENOTES] Failed to fetch PR #$prNum : $_"
        }
    }

    $copilotCount = ($prDetails | Where-Object { $_.CopilotReview }).Count
    Write-Host "[RELEASENOTES] Fetched details for $($prDetails.Count) PRs ($copilotCount with Copilot reviews)" -ForegroundColor Green

    # Log all fetched PR details
    $prDebug = ""
    foreach ($pr in $prDetails) {
        $prDebug += "PR #$($pr.Number) — $($pr.Title)`n"
        $prDebug += "  Author: $($pr.Author)  |  Labels: $($pr.Labels)`n"
        $prDebug += "  Body: $(if ($pr.Body) { "$($pr.Body.Length) chars" } else { '(empty)' })`n"
        $prDebug += "  Copilot Review: $(if ($pr.CopilotReview) { "$($pr.CopilotReview.Length) chars" } else { '(none)' })`n"
        if ($pr.CopilotReview) {
            $prDebug += "  --- Copilot Review Content ---`n$($pr.CopilotReview)`n  --- End Copilot Review ---`n"
        }
        $prDebug += "`n"
    }
    Write-DebugLog -Label "PR DETAILS ($($prDetails.Count) PRs, $copilotCount with Copilot reviews)" -Content $prDebug
}

# --- Step 4: AI summarization via GitHub Models ---
if (-not $SkipAI -and $ModelsToken) {
    Write-Host "[RELEASENOTES] Generating AI-powered release notes via GitHub Models ($Model)..." -ForegroundColor Cyan

    $systemPrompt = @"
You are a technical writer for winapp CLI, a command-line tool that helps Windows app developers manage SDKs, packaging, app identity, manifests, certificates, and build tools.

You write release notes for GitHub releases. Your output is Markdown.

Important: The brand name is always lowercase "winapp CLI" — never "WinApp CLI", "Winapp CLI", or "WINAPP CLI".

Style guidelines:
- Group entries into these categories in this order (omit empty categories):
  ## New Features
  ## Breaking Changes
  ## Bug Fixes
  ## Documentation
  ## Infrastructure
- A PR can appear in multiple categories. For example, a PR that is both a new feature and a breaking change should appear in both New Features (describing the feature) and Breaking Changes (describing what breaks and how to migrate).
- Within each category, order entries by importance/magnitude — major new capabilities (new commands, new technology support) come before smaller additions (new flags, options).
- Each entry: bold feature/fix name, PR number in parentheses, em-dash, 1-3 sentence description focused on user impact
- Example entry format: - **Feature name** (#123) — Description of what changed and why it matters.
- In Breaking Changes, focus on what existing behavior changed and what users need to do to migrate.
- If a PR description includes code snippets or usage examples, include a short code block showing the new command or usage. Keep snippets to 1-3 lines max.
- Consolidate dependency bumps into a single Infrastructure bullet listing package names and PR numbers
- Skip version bump PRs (e.g., "Bump version from X to Y")
- Use backticks for command names, file names, flags, and code
- Do NOT include a title or preamble — start directly with the first ## heading
- Do NOT include a Full Changelog link
"@

    $prContext = ""
    foreach ($pr in $prDetails) {
        $prContext += "PR #$($pr.Number) by @$($pr.Author)"
        if ($pr.Labels) { $prContext += " [$($pr.Labels)]" }
        $prContext += "`nTitle: $($pr.Title)`n"
        if ($pr.Body -and $pr.Author -ne "dependabot[bot]") {
            $prContext += "Description: $($pr.Body)`n"
        }
        if ($pr.CopilotReview) {
            $prContext += "Copilot Review: $($pr.CopilotReview)`n"
        }
        $prContext += "---`n"
    }

    # GitHub Models free tier token limits vary by model (~8K tokens for gpt-4o-mini).
    # ~4 chars per token, reserve space for system prompt + max_tokens output.
    $maxPromptChars = 24000
    $systemChars = $systemPrompt.Length
    $availableForUser = $maxPromptChars - $systemChars

    $userPromptHeader = @"
Generate release notes for WinApp CLI $CurrentTag (previous: $PreviousTag).

GitHub auto-generated changelog:
$rawNotes

Detailed PR information:
"@

    $remainingChars = $availableForUser - $userPromptHeader.Length
    if ($prContext.Length -gt $remainingChars) {
        Write-Warning "[RELEASENOTES] PR context ($($prContext.Length) chars) exceeds token budget ($remainingChars chars available). Truncating."
        $prContext = $prContext.Substring(0, $remainingChars) + "`n... (truncated — $($prDetails.Count) PRs total, some details omitted to fit token limit)`n"
    }

    $userPrompt = $userPromptHeader + $prContext

    Write-DebugLog -Label "SYSTEM PROMPT" -Content $systemPrompt
    Write-DebugLog -Label "USER PROMPT ($($userPrompt.Length) chars, limit $availableForUser)" -Content $userPrompt

    try {
        $aiHeaders = @{
            "Authorization" = "Bearer $ModelsToken"
            "Content-Type"  = "application/json"
        }

        $aiBody = @{
            model      = $Model
            max_tokens = 4096
            messages   = @(
                @{ role = "system"; content = $systemPrompt },
                @{ role = "user"; content = $userPrompt }
            )
        } | ConvertTo-Json -Depth 10

        $aiResponse = Invoke-RestMethod `
            -Uri "https://models.github.ai/inference/chat/completions" `
            -Method Post `
            -Headers $aiHeaders `
            -Body $aiBody `
            -ContentType "application/json"

        $aiNotes = $aiResponse.choices[0].message.content

        Write-DebugLog -Label "AI RESPONSE ($($aiNotes.Length) chars)" -Content $aiNotes

        if ($aiNotes -and $aiNotes.Trim().Length -gt 50) {
            Write-Host "[RELEASENOTES] AI generation successful ($($aiNotes.Length) chars)" -ForegroundColor Green
            $rawNotes = $aiNotes
        }
        else {
            Write-Warning "[RELEASENOTES] AI returned empty or too-short response. Using raw notes."
        }
    }
    catch {
        Write-Warning "[RELEASENOTES] AI generation failed: $_"
        Write-Warning "[RELEASENOTES] Falling back to GitHub-generated notes."
    }
}
elseif ($SkipAI) {
    Write-Host "[RELEASENOTES] Skipping AI summarization (-SkipAI)" -ForegroundColor Yellow
}

# --- Step 5: Write output ---
$rawNotes | Set-Content -Path $OutputPath -Encoding UTF8 -NoNewline
Write-Host "[RELEASENOTES] Release notes written to: $OutputPath" -ForegroundColor Green

# Also output to stdout for pipeline consumption
Write-Output $rawNotes
