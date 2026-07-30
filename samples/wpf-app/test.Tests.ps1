param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe 'wpf-app sample' {

    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:originalLocation = Get-Location

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        Set-Location $script:sampleDir

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'Phase 1: WPF Guide Workflow (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'wpf-guide'
                Push-Location $script:tempDir

                Invoke-Expression 'dotnet new wpf -n test-wpf-app'
                $script:dotnetNewExit = $LASTEXITCODE

                if ($script:dotnetNewExit -eq 0) {
                    Push-Location 'test-wpf-app'
                }
            }
        }

        AfterAll {
            if (-not $script:skip) {
                # Unwind any Push-Location calls made during this context
                Set-Location $script:originalLocation
            }
        }

        It 'Creates a new WPF project' -Skip:$script:skip {
            $script:dotnetNewExit | Should -Be 0
        }

        It 'Runs winapp init successfully' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'init --use-defaults'
        }

        It 'Generates Package.appxmanifest from winapp init' -Skip:$script:skip {
            'Package.appxmanifest' | Should -Exist
        }

        It 'Builds in Debug mode' -Skip:$script:skip {
            Invoke-Expression 'dotnet build -c Debug /p:ApplyDebugIdentity=false'
            $LASTEXITCODE | Should -Be 0
        }

        It 'Applies debug identity with create-debug-identity' -Skip:$script:skip {
            $exeFile = Get-ChildItem -Path 'bin\Debug' -Filter '*.exe' -Recurse | Select-Object -First 1
            $exeFile | Should -Not -BeNullOrEmpty
            Invoke-WinappCommand -Arguments "create-debug-identity `"$($exeFile.FullName)`""
        }

        It 'Registers app with winapp run --no-launch' -Skip:$script:skip {
            $exeFile = Get-ChildItem -Path 'bin\Debug' -Filter '*.exe' -Recurse | Select-Object -First 1
            Invoke-WinappCommand -Arguments "run `"$($exeFile.DirectoryName)`" --no-launch"
        }

        It 'Generates a dev certificate' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'cert generate --if-exists skip'
            'devcert.pfx' | Should -Exist
        }

        It 'Shows certificate info without error' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'cert info devcert.pfx'
        }

        It 'Builds in Release mode with RID' -Skip:$script:skip {
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
            Invoke-Expression "dotnet build -c Release -r $rid"
            $LASTEXITCODE | Should -Be 0
        }

        It 'Packages MSIX with winapp pack' -Skip:$script:skip {
            $exeFile = Get-ChildItem -Path 'bin\Release' -Filter '*.exe' -Recurse | Select-Object -First 1
            $exeFile | Should -Not -BeNullOrEmpty -Because 'Release build should produce an .exe'
            $script:outputDir = $exeFile.DirectoryName

            Invoke-WinappCommand -Arguments "pack `"$($script:outputDir)`" --manifest Package.appxmanifest --cert devcert.pfx"
        }

        It 'Produces an MSIX file' -Skip:$script:skip {
            $msix = Get-ChildItem -Path '.' -Filter '*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1
            $msix | Should -Not -BeNullOrEmpty -Because 'winapp pack should create an .msix file'
        }
    }

    Context 'Phase 2: Sample Build Check' {

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

        It 'Restores NuGet packages' -Skip:$script:skip {
            Invoke-Expression 'dotnet restore'
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds existing sample in Debug mode' -Skip:$script:skip {
            Invoke-Expression 'dotnet build -c Debug /p:ApplyDebugIdentity=false'
            $LASTEXITCODE | Should -Be 0
        }
    }
}
