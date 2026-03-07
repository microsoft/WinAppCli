<#
.SYNOPSIS
Pester 5.x tests for the flutter-app sample and Flutter guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/flutter.md guide from scratch — creates a new
  Flutter project, runs winapp init, builds, and packages as MSIX.
Phase 2: Quick build of the existing sample to verify it is not stale.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command flutter -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "flutter-app sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command flutter -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:projectDir = $null

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        Set-Location $script:sampleDir
        if (-not $SkipCleanup -and -not $script:skip) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir "build") -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Phase 1: Flutter Guide Workflow (from scratch)" -Skip:$script:skip {
        BeforeAll {
            $script:tempDir = New-TempTestDirectory -Prefix "flutter-guide"
            Set-Location $script:tempDir

            flutter create test_flutter_app --platforms=windows
            if ($LASTEXITCODE -ne 0) { throw "flutter create failed" }

            $script:projectDir = Join-Path $script:tempDir "test_flutter_app"
            Set-Location $script:projectDir

            Invoke-WinappCommand "init --use-defaults --setup-sdks=stable"

            flutter build windows
            if ($LASTEXITCODE -ne 0) { throw "flutter build windows failed" }

            $script:buildOutput = Join-Path $script:projectDir "build\windows\x64\runner\Release"
            Copy-Item $script:buildOutput -Destination (Join-Path $script:projectDir "dist") -Recurse

            Invoke-WinappCommand "cert generate --if-exists skip"
            Invoke-WinappCommand "pack dist --cert devcert.pfx"
        }

        It "Should create winapp.yaml after init" {
            Join-Path $script:projectDir "winapp.yaml" | Should -Exist
        }

        It "Should create appxmanifest.xml after init" {
            Join-Path $script:projectDir "appxmanifest.xml" | Should -Exist
        }

        It "Should create .winapp directory after init" {
            Join-Path $script:projectDir ".winapp" | Should -Exist
        }

        It "Should produce Flutter build output" {
            $script:buildOutput | Should -Exist
        }

        It "Should generate a dev certificate" {
            Join-Path $script:projectDir "devcert.pfx" | Should -Exist
        }

        It "Should produce an MSIX package" {
            Get-ChildItem -Path $script:projectDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
        }
    }

    Context "Phase 2: Sample Build Check" -Skip:$script:skip {
        BeforeAll {
            Set-Location $script:sampleDir

            flutter pub get
            if ($LASTEXITCODE -ne 0) { throw "flutter pub get failed" }

            Invoke-WinappCommand "restore"

            flutter build windows
            if ($LASTEXITCODE -ne 0) { throw "flutter build windows failed" }
        }

        It "Should build flutter_app.exe" {
            Join-Path $script:sampleDir "build\windows\x64\runner\Release\flutter_app.exe" | Should -Exist
        }
    }
}
