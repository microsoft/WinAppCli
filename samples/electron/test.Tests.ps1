param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Electron Sample Freshness Check" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:originalLocation = Get-Location
    }

    AfterAll {
        Set-Location $script:sampleDir

        if (-not $SkipCleanup) {
            Remove-Item -Path (Join-Path $script:sampleDir 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Prerequisites" {
        It "Should have Node.js available" -Skip:$script:skip {
            Test-Prerequisite 'node' | Should -Be $true
        }

        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }
    }

    Context "Sample Build Check" {
        BeforeAll {
            if (-not $script:skip) {
                Push-Location $script:sampleDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It "Should install sample dependencies" -Skip:$script:skip {
            Invoke-Expression "npm install --ignore-scripts"
            $LASTEXITCODE | Should -Be 0
        }

        It "Should have created node_modules" -Skip:$script:skip {
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
    }
}
