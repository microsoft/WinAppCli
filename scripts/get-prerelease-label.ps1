<#
.SYNOPSIS
    Returns a prerelease label based on the current git branch.

.DESCRIPTION
    Determines the prerelease label for versioning based on the current git branch.
    - main and rel/* branches return "prerelease" (the default label)
    - All other branches return a sanitized branch name (e.g., dev/my-feature -> dev-my-feature)
    - Detached HEAD or unknown branches return "prerelease"

    The branch name is sanitized for semver compatibility:
    - Only [A-Za-z0-9-] characters are kept (others replaced with -)
    - Consecutive dashes are collapsed
    - Truncated to 40 characters

.OUTPUTS
    Returns the prerelease label as a string.

.EXAMPLE
    .\get-prerelease-label.ps1
    # On main: returns "prerelease"
    # On dev/my-feature: returns "dev-my-feature"
#>

$CurrentBranch = git branch --show-current 2>$null
if ([string]::IsNullOrEmpty($CurrentBranch)) {
    # Detached HEAD (e.g., CI checkout) - fall back
    $CurrentBranch = git rev-parse --abbrev-ref HEAD 2>$null
}

if ([string]::IsNullOrEmpty($CurrentBranch) -or $CurrentBranch -eq 'HEAD') {
    # CI environments check out in detached HEAD - use CI environment variables
    if ($env:GITHUB_HEAD_REF) {
        # GitHub Actions: PR source branch
        $CurrentBranch = $env:GITHUB_HEAD_REF
    } elseif ($env:GITHUB_REF_NAME) {
        # GitHub Actions: push branch
        $CurrentBranch = $env:GITHUB_REF_NAME
    } elseif ($env:BUILD_SOURCEBRANCH) {
        # Azure DevOps: strip refs/heads/ prefix
        $CurrentBranch = $env:BUILD_SOURCEBRANCH -replace '^refs/heads/', ''
    }
}

if ([string]::IsNullOrEmpty($CurrentBranch) -or $CurrentBranch -eq 'HEAD') {
    Write-Output "prerelease"
} elseif ($CurrentBranch -eq 'main' -or $CurrentBranch -match '^rel/') {
    Write-Output "prerelease"
} else {
    # Sanitize branch name for semver: only [A-Za-z0-9-] allowed in prerelease identifiers
    $label = $CurrentBranch -replace '[^A-Za-z0-9-]', '-'  # replace illegal chars with dash
    $label = $label -replace '-+', '-'                      # collapse consecutive dashes
    $label = $label.Trim('-')                                # trim leading/trailing dashes
    if ($label.Length -gt 40) {
        $label = $label.Substring(0, 40).TrimEnd('-')
    }
    if ([string]::IsNullOrEmpty($label)) {
        $label = "prerelease"
    }
    Write-Output $label
}
