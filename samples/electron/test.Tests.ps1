<#
.SYNOPSIS
Pester 5.x tests for the Electron sample and guide workflow.

.DESCRIPTION
Phase 1: Follows the Electron guide from scratch — scaffolds an Electron app,
  installs winapp, initializes workspace, creates and builds C#/C++ addons,
  packages the app, and creates a signed MSIX package.
Phase 2: Quick install of the existing sample to verify it is not stale.

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
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Electron Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:appDir = $null
        $script:resolvedPkg = $null

        if (-not $script:skip) {
            $script:resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Phase 1: Electron Guide Workflow (from scratch)" {
        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix "electron-guide"

                # Use a dedicated npm cache to avoid ECOMPROMISED errors in CI
                $npmCacheDir = Join-Path $script:tempDir ".npm-cache"
                $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
                $env:npm_config_cache = $npmCacheDir
            }
        }

        It "Should create a new Electron app" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                $maxRetries = 3
                $created = $false
                for ($i = 1; $i -le $maxRetries; $i++) {
                    if ($i -gt 1) {
                        Remove-Item -Path (Join-Path $script:tempDir "electron-app") -Recurse -Force -ErrorAction SilentlyContinue
                        Invoke-Expression "npm cache clean --force" 2>$null
                        Start-Sleep -Seconds 2
                    }
                    Invoke-Expression "npx -y create-electron-app@latest electron-app"
                    if ($LASTEXITCODE -eq 0) { $created = $true; break }
                }
                $created | Should -Be $true -Because "Electron app creation should succeed within $maxRetries attempts"
                $script:appDir = Join-Path $script:tempDir "electron-app"
                $script:appDir | Should -Exist
            } finally { Pop-Location }
        }

        It "Should configure package.json for MSIX" -Skip:$script:skip {
            $pkgPath = Join-Path $script:appDir "package.json"
            $pkg = Get-Content $pkgPath | ConvertFrom-Json
            $pkg | Add-Member -MemberType NoteProperty -Name "displayName" -Value "WinApp Electron Test" -Force
            $pkg | Add-Member -MemberType NoteProperty -Name "description" -Value "Test app for winapp CLI" -Force
            if ([string]::IsNullOrEmpty($pkg.version)) { $pkg.version = "1.0.0" }
            $pkg | ConvertTo-Json -Depth 10 | Set-Content $pkgPath
            $pkgPath | Should -Exist
        }

        It "Should install winapp as a local devDependency" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Install-WinappNpmPackage -PackagePath $script:resolvedPkg
                Join-Path $script:appDir "node_modules\.bin\winapp.cmd" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should initialize winapp workspace" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "init . --use-defaults --setup-sdks=stable"
            } finally { Pop-Location }
        }

        It "Should create workspace files" -Skip:$script:skip {
            Join-Path $script:appDir ".winapp" | Should -Exist
            Join-Path $script:appDir "winapp.yaml" | Should -Exist
            Join-Path $script:appDir "Package.appxmanifest" | Should -Exist
        }

        It "Should create a C++ native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cpp --name testCppAddon"
                Join-Path $script:appDir "testCppAddon" | Should -Exist
                Join-Path $script:appDir "testCppAddon\binding.gyp" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should create a C# native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cs --name testCsAddon"
                Join-Path $script:appDir "testCsAddon" | Should -Exist
                Join-Path $script:appDir "testCsAddon\testCsAddon.csproj" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should build the C++ addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $output = Invoke-Expression "npx node-gyp clean configure build --directory=testCppAddon --verbose 2>&1"
                $output | ForEach-Object { Write-Host $_ }
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should build the C# addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-Expression "npm run build-testCsAddon"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should add Electron debug identity" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node add-electron-debug-identity --no-install"
            } finally { Pop-Location }
        }

        It "Should package the Electron app" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-Expression "npm run package"
                $LASTEXITCODE | Should -Be 0
                $script:outDir = Join-Path $script:appDir "out"
                $script:outDir | Should -Exist
                $script:appPackageDir = (Get-ChildItem -Path $script:outDir -Directory | Select-Object -First 1).FullName
                $script:appPackageDir | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }

        It "Should register app with winapp run --no-launch" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "run `"$($script:appPackageDir)`" --no-launch"
            } finally { Pop-Location }
        }

        It "Should generate a development certificate" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "cert generate"
                Join-Path $script:appDir "devcert.pfx" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $certPath = Join-Path $script:appDir "devcert.pfx"
                Invoke-WinappCommand -Arguments "pack `"$($script:appPackageDir)`" --cert `"$certPath`""
                Get-ChildItem -Path $script:appDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }
    }

    Context "Phase 2: Sample Build Check" {
        It "Should install sample dependencies" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "npm install --ignore-scripts"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have node_modules" -Skip:$script:skip {
            Join-Path $script:sampleDir 'node_modules' | Should -Exist
        }

        It "Should have package.json" -Skip:$script:skip {
            Join-Path $script:sampleDir 'package.json' | Should -Exist
        }

        It "Should have forge.config.js" -Skip:$script:skip {
            Join-Path $script:sampleDir 'forge.config.js' | Should -Exist
        }

        It "Should have appxmanifest.xml" -Skip:$script:skip {
            Join-Path $script:sampleDir 'appxmanifest.xml' | Should -Exist
        }

        It "Should build the C# addon" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "npm run build-csAddon"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }
    }
}
