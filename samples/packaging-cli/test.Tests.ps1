param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Packaging CLI Guide Workflow" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $script:tempDir = $null

        if ($script:skip) { return }

        $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        Install-WinappGlobal -PackagePath $resolvedPkg

        $script:tempDir = New-TempTestDirectory -Prefix "packaging-cli-guide"
        $script:packageDir = Join-Path $script:tempDir "MyCliPackage"
        $null = New-Item -ItemType Directory -Path $script:packageDir -Force
        Copy-Item "$env:SystemRoot\System32\cmd.exe" -Destination (Join-Path $script:packageDir "mycli.exe")
    }

    AfterAll {
        if (-not $SkipCleanup -and $script:tempDir) {
            Remove-TempTestDirectory -Path $script:tempDir
        }
    }

    Context "Prerequisites" {
        It "Should have npm available" -Skip:$script:skip {
            Test-Prerequisite 'npm' | Should -Be $true
        }

        It "Should have dummy CLI executable" -Skip:$script:skip {
            Join-Path $script:packageDir "mycli.exe" | Should -Exist
        }
    }

    Context "Manifest Generation" {
        It "Should generate manifest from executable" -Skip:$script:skip {
            Push-Location $script:packageDir
            try {
                Invoke-WinappCommand -Arguments "manifest generate --executable mycli.exe"
            } finally { Pop-Location }
        }

        It "Should have created Package.appxmanifest" -Skip:$script:skip {
            Join-Path $script:packageDir "Package.appxmanifest" | Should -Exist
        }
    }

    Context "Certificate Generation" {
        It "Should generate dev certificate" -Skip:$script:skip {
            Push-Location $script:packageDir
            try {
                Invoke-WinappCommand -Arguments "cert generate --if-exists skip"
            } finally { Pop-Location }
        }

        It "Should have created devcert.pfx" -Skip:$script:skip {
            Join-Path $script:packageDir "devcert.pfx" | Should -Exist
        }

        It "Should report certificate info" -Skip:$script:skip {
            Push-Location $script:packageDir
            try {
                $output = Invoke-WinappCommand -Arguments "cert info devcert.pfx"
                $output | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }
    }

    Context "MSIX Packaging and Signing" {
        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:packageDir
            try {
                Invoke-WinappCommand -Arguments "pack . --cert devcert.pfx"
            } finally { Pop-Location }
        }

        It "Should have created an MSIX file" -Skip:$script:skip {
            $msix = Get-ChildItem -Path $script:packageDir -Filter "*.msix" |
                Select-Object -First 1
            $msix | Should -Not -BeNullOrEmpty
            $script:msixPath = $msix.FullName
        }

        It "Should sign the MSIX" -Skip:$script:skip {
            Push-Location $script:packageDir
            try {
                Invoke-WinappCommand -Arguments "sign `"$($script:msixPath)`" devcert.pfx"
            } finally { Pop-Location }
        }
    }
}
