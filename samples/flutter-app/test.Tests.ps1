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
        }

        It "Should create a new Flutter project" {
            flutter create test_flutter_app --platforms=windows
            $LASTEXITCODE | Should -Be 0
            $script:projectDir = Join-Path $script:tempDir "test_flutter_app"
            $script:projectDir | Should -Exist
        }

        It "Should run winapp init successfully" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "init --use-defaults --setup-sdks=stable"
        }

        It "Should create winapp.yaml after init" {
            Join-Path $script:projectDir "winapp.yaml" | Should -Exist
        }

        It "Should create Package.appxmanifest after init" {
            Join-Path $script:projectDir "Package.appxmanifest" | Should -Exist
        }

        It "Should create .winapp directory after init" {
            Join-Path $script:projectDir ".winapp" | Should -Exist
        }

        It "Should build Flutter app for Windows" {
            Set-Location $script:projectDir
            flutter build windows
            $LASTEXITCODE | Should -Be 0
            $script:buildOutput = Join-Path $script:projectDir "build\windows\x64\runner\Release"
            $script:buildOutput | Should -Exist
        }

        It "Should run app with identity via winapp run" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "run $($script:buildOutput) --no-launch"
        }

        It "Should generate a dev certificate" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "cert generate --if-exists skip"
            Join-Path $script:projectDir "devcert.pfx" | Should -Exist
        }

        It "Should prepare dist directory" {
            Set-Location $script:projectDir
            Copy-Item $script:buildOutput -Destination (Join-Path $script:projectDir "dist") -Recurse
            Join-Path $script:projectDir "dist" | Should -Exist
        }

        It "Should package as MSIX" {
            Set-Location $script:projectDir
            Invoke-WinappCommand -Arguments "pack dist --cert devcert.pfx"
            Get-ChildItem -Path $script:projectDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
        }
    }

    Context "Phase 2: Sample Build Check" -Skip:$script:skip {
        BeforeAll {
            Set-Location $script:sampleDir

            flutter pub get
            if ($LASTEXITCODE -ne 0) { throw "flutter pub get failed" }

            Invoke-WinappCommand -Arguments "restore"

            flutter build windows
            if ($LASTEXITCODE -ne 0) { throw "flutter build windows failed" }
        }

        It "Should build flutter_app.exe" {
            Join-Path $script:sampleDir "build\windows\x64\runner\Release\flutter_app.exe" | Should -Exist
        }
    }
}
