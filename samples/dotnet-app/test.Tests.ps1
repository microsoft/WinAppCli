param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe ".NET App Guide Workflow" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $script:tempDir = $null
        $script:sampleDir = $PSScriptRoot

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg

        $script:tempDir = New-TempTestDirectory -Prefix "dotnet-guide"
        $script:projectDir = Join-Path $script:tempDir "test-dotnet-app"
    }

    AfterAll {
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            if ($script:sampleDir) {
                Remove-Item -Path (Join-Path $script:sampleDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $script:sampleDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context "Prerequisites" {
        It "Should have dotnet available" -Skip:$script:skip {
            Test-Prerequisite 'dotnet' | Should -Be $true
        }

        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }
    }

    Context "Phase 1: .NET Guide Workflow (from scratch)" {

        Context "Project Creation" {
            It "Should create a new .NET console project" -Skip:$script:skip {
                Push-Location $script:tempDir
                try {
                    Invoke-Expression "dotnet new console -n test-dotnet-app"
                    $LASTEXITCODE | Should -Be 0
                } finally { Pop-Location }
            }

            It "Should have created the project directory" -Skip:$script:skip {
                $script:projectDir | Should -Exist
            }
        }

        Context "Winapp Init" {
            It "Should run winapp init successfully" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-WinappCommand -Arguments "init --use-defaults"
                } finally { Pop-Location }
            }

            It "Should have created Package.appxmanifest" -Skip:$script:skip {
                Join-Path $script:projectDir "Package.appxmanifest" | Should -Exist
            }
        }

        Context "Debug with Identity" {
            It "Should build in Debug mode" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-Expression "dotnet build -c Debug"
                    $LASTEXITCODE | Should -Be 0
                } finally { Pop-Location }
            }

            It "Should apply debug identity with create-debug-identity" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    $exeFile = Get-ChildItem -Path (Join-Path $script:projectDir "bin\Debug") -Filter "*.exe" -Recurse |
                        Select-Object -First 1
                    $exeFile | Should -Not -BeNullOrEmpty
                    Invoke-WinappCommand -Arguments "create-debug-identity `"$($exeFile.FullName)`""
                } finally { Pop-Location }
            }

            It "Should add execution alias to manifest" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-WinappCommand -Arguments "manifest add-alias"
                } finally { Pop-Location }
            }

            It "Should run app with identity via winapp run" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    $debugDir = Get-ChildItem -Path (Join-Path $script:projectDir "bin\Debug") -Filter "*.exe" -Recurse |
                        Select-Object -First 1
                    Invoke-WinappCommand -Arguments "run `"$($debugDir.DirectoryName)`" --unregister-on-exit"
                } finally { Pop-Location }
            }
        }

        Context "Certificate Generation" {
            It "Should generate dev certificate" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-WinappCommand -Arguments "cert generate --if-exists skip"
                } finally { Pop-Location }
            }

            It "Should have created devcert.pfx" -Skip:$script:skip {
                Join-Path $script:projectDir "devcert.pfx" | Should -Exist
            }

            It "Should report certificate info" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    $output = Invoke-WinappCommand -Arguments "cert info devcert.pfx"
                    $output | Should -Not -BeNullOrEmpty
                } finally { Pop-Location }
            }
        }

        Context "Release Build and MSIX Packaging" {
            It "Should build in Release mode" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-Expression "dotnet build -c Release"
                    $LASTEXITCODE | Should -Be 0
                } finally { Pop-Location }
            }

            It "Should produce a Release executable" -Skip:$script:skip {
                $exeFile = Get-ChildItem -Path (Join-Path $script:projectDir "bin\Release") -Filter "*.exe" -Recurse |
                    Select-Object -First 1
                $exeFile | Should -Not -BeNullOrEmpty
                $script:outputDir = $exeFile.DirectoryName
            }

            It "Should package MSIX with winapp pack" -Skip:$script:skip {
                Push-Location $script:projectDir
                try {
                    Invoke-WinappCommand -Arguments "pack `"$($script:outputDir)`" --manifest Package.appxmanifest --cert devcert.pfx"
                } finally { Pop-Location }
            }

            It "Should have created an MSIX file" -Skip:$script:skip {
                $msix = Get-ChildItem -Path $script:projectDir -Filter "*.msix" |
                    Select-Object -First 1
                $msix | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context "Phase 2: Sample Build Check" {
        It "Should restore sample dependencies" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "dotnet restore"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should build sample in Debug mode" -Skip:$script:skip {
            Push-Location $script:sampleDir
            try {
                Invoke-Expression "dotnet build -c Debug /p:ApplyDebugIdentity=false"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }
    }
}
