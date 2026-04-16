param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue) -or $null -eq (Get-Command cargo -ErrorAction SilentlyContinue)
}

Describe "Tauri App Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue) -or $null -eq (Get-Command cargo -ErrorAction SilentlyContinue)
        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg

        $script:tempDir = New-TempTestDirectory -Prefix "tauri-guide"
        $script:tempApp = Join-Path $script:tempDir "tauri-app"
    }

    AfterAll {
        Set-Location $script:sampleDir

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir "src-tauri\target") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Prerequisites" {
        It "Should have Node.js available" -Skip:$script:skip {
            Test-Prerequisite 'node' | Should -Be $true
        }

        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }

        It "Should have Rust/Cargo available" -Skip:$script:skip {
            Test-Prerequisite 'cargo' | Should -Be $true
        }
    }

    Context "Tauri Guide Workflow (from scratch)" {
        It "Should copy sample to temp directory" -Skip:$script:skip {
            Copy-Item -Path $script:sampleDir -Destination $script:tempApp -Recurse -Exclude @('.gitignore', 'node_modules', 'src-tauri\target')
            $script:tempApp | Should -Exist
        }

        It "Should install npm dependencies" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-Expression "npm install"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should run winapp init" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-WinappCommand -Arguments "init --use-defaults --setup-sdks=none"
            } finally { Pop-Location }
        }

        It "Should have appxmanifest" -Skip:$script:skip {
            # winapp init preserves the existing appxmanifest.xml copied from the sample
            Join-Path $script:tempApp "appxmanifest.xml" | Should -Exist
        }

        It "Should build Tauri app in debug mode" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-Expression "cargo build --manifest-path src-tauri\Cargo.toml"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should run app with identity via winapp run" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                $distDir = Join-Path $script:tempApp "dist"
                $null = New-Item -ItemType Directory -Path $distDir -Force
                Copy-Item (Join-Path $script:tempApp "src-tauri\target\debug\tauri-app.exe") -Destination $distDir
                Invoke-WinappCommand -Arguments "run dist --no-launch"
            } finally { Pop-Location }
        }

        It "Should build Tauri app in release mode" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-Expression "cargo build --release --manifest-path src-tauri\Cargo.toml"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have produced release executable" -Skip:$script:skip {
            Join-Path $script:tempApp "src-tauri\target\release\tauri-app.exe" | Should -Exist
        }

        It "Should prepare MSIX layout" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                $layoutDir = Join-Path $script:tempApp "msix-layout"
                $null = New-Item -ItemType Directory -Path $layoutDir -Force
                $tauriExe = Join-Path $script:tempApp "src-tauri\target\release\tauri-app.exe"
                Copy-Item $tauriExe -Destination $layoutDir
                Join-Path $layoutDir "tauri-app.exe" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should generate dev certificate" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-WinappCommand -Arguments "cert generate --if-exists skip --manifest appxmanifest.xml"
            } finally { Pop-Location }
        }

        It "Should have created devcert.pfx" -Skip:$script:skip {
            Join-Path $script:tempApp "devcert.pfx" | Should -Exist
        }

        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:tempApp
            try {
                Invoke-WinappCommand -Arguments "pack msix-layout --manifest appxmanifest.xml --cert devcert.pfx"
            } finally { Pop-Location }
        }

        It "Should have created an MSIX file" -Skip:$script:skip {
            $msix = Get-ChildItem -Path $script:tempApp -Filter "*.msix" |
                Select-Object -First 1
            $msix | Should -Not -BeNullOrEmpty
        }
    }

    Context "Sample Build Check" {
        It "Should install sample npm dependencies" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "npm install"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should build sample Rust backend" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "cargo build --manifest-path src-tauri\Cargo.toml"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have produced debug executable" -Skip:$script:skip {
            Join-Path $script:sampleDir "src-tauri\target\debug\tauri-app.exe" | Should -Exist
        }
    }
}
