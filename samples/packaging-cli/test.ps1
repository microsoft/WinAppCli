<#
.SYNOPSIS
Test script for the packaging-cli guide workflow.

.DESCRIPTION
Follows docs/guides/packaging-cli.md from scratch — takes a pre-built CLI
executable, generates a manifest, creates a certificate, packages as MSIX,
and signs the package. Tests winapp manifest generate, cert generate,
cert info, pack, and sign commands.

This test has no corresponding sample — it exists purely to validate the
generic "package any CLI" guide.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.

.PARAMETER Verbose
Enable verbose output.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup,
    [switch]$Verbose
)

Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

$ctx = New-SampleTestContext -SampleName "packaging-cli" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "npm" -DisplayName "npm"

    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Guide Workflow — Package a CLI Executable as MSIX
    # ==================================================================
    Write-TestHeader "Packaging CLI Guide Workflow"

    $tempDir = New-TempTestDirectory -Prefix "packaging-cli-guide"
    Push-Location $tempDir

    # Create a minimal dummy executable (copy cmd.exe as stand-in)
    Write-TestStep "Preparing dummy CLI executable..." (++$step)
    $null = New-Item -ItemType Directory -Path "MyCliPackage" -Force
    Copy-Item "$env:SystemRoot\System32\cmd.exe" -Destination "MyCliPackage\mycli.exe"
    Assert-FileExists "MyCliPackage\mycli.exe" "Dummy CLI executable"

    Push-Location "MyCliPackage"

    # Generate manifest from executable (core guide step)
    Write-TestStep "Generating manifest from executable..." (++$step)
    Assert-Command "winapp manifest generate --executable mycli.exe" "winapp manifest generate failed"
    Assert-FileExists "appxmanifest.xml" "Generated AppxManifest"

    # Generate certificate
    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "cert generate failed"
    Assert-FileExists "devcert.pfx" "Development certificate"

    # Verify certificate info (guides show this for verification)
    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath "devcert.pfx"

    # Package as MSIX
    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack . --cert devcert.pfx" "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    $msixPath = Assert-MsixCreated -Directory (Get-Location) -Description "Packaging-CLI MSIX"

    # Sign the MSIX (standalone sign command from usage.md)
    Write-TestStep "Signing MSIX (standalone sign command)..." (++$step)
    Assert-Command "winapp sign `"$msixPath`" --cert devcert.pfx" "winapp sign failed"
    Write-TestSuccess "MSIX signed successfully"

    Pop-Location  # back to tempDir
    Pop-Location  # back to original

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
    }
}
