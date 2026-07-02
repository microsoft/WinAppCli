param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe 'sparse-app sample' {

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
            Remove-Item -Path (Join-Path $script:sampleDir 'devcert.pfx') -Force -ErrorAction SilentlyContinue
            Get-ChildItem -Path $script:sampleDir -Filter '*.msix' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'Phase 1: Sparse Packaging Guide Workflow (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'sparse-guide'
                Push-Location $script:tempDir

                Invoke-Expression 'dotnet new wpf -n test-sparse-app'
                $script:dotnetNewExit = $LASTEXITCODE

                if ($script:dotnetNewExit -eq 0) {
                    Push-Location 'test-sparse-app'
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

        It 'Builds the app in Debug mode' -Skip:$script:skip {
            Invoke-Expression 'dotnet build -c Debug'
            $LASTEXITCODE | Should -Be 0
        }

        It 'Step 1: Generates a sparse manifest with winapp init --exe --sparse' -Skip:$script:skip {
            $exeFile = Get-ChildItem -Path 'bin\Debug' -Filter 'test-sparse-app.exe' -Recurse | Select-Object -First 1
            $exeFile | Should -Not -BeNullOrEmpty -Because 'Debug build should produce an .exe'
            $script:exePath = $exeFile.FullName

            Invoke-WinappCommand -Arguments "init --exe `"$($script:exePath)`" --sparse --use-defaults --name SparseGuideApp --publisher `"CN=Sparse Guide`""
        }

        It 'Generates a sparse appxmanifest.xml next to the exe' -Skip:$script:skip {
            $manifest = Join-Path (Split-Path $script:exePath -Parent) 'appxmanifest.xml'
            $manifest | Should -Exist
            $script:manifestPath = $manifest
            $content = Get-Content $manifest -Raw
            $content | Should -Match 'AllowExternalContent'
            $content | Should -Match 'win32App'
            $content | Should -Match 'ProcessorArchitecture="neutral"'
        }

        It 'Skips SDK installation (no winapp.yaml created)' -Skip:$script:skip {
            'winapp.yaml' | Should -Not -Exist
        }

        It 'Step 2a: Generates a dev certificate' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'cert generate --publisher "CN=Sparse Guide" --if-exists skip'
            'devcert.pfx' | Should -Exist
        }

        It 'Step 2b: Packs the identity-only MSIX with winapp pack' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "pack `"$($script:manifestPath)`" --cert devcert.pfx --output `"$(Join-Path (Get-Location) 'SparseGuideApp.identity.msix')`""
        }

        It 'Produces an identity .msix file' -Skip:$script:skip {
            'SparseGuideApp.identity.msix' | Should -Exist
        }

        It 'Step 3: Embeds identity into the exe with winapp embed-identity' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "embed-identity `"$($script:exePath)`" --manifest `"$($script:manifestPath)`""
        }

        It 'Supports embed-identity in XML mode' -Skip:$script:skip {
            $xmlManifest = Join-Path (Get-Location) 'app.manifest'
            Invoke-WinappCommand -Arguments "embed-identity `"$xmlManifest`" --manifest `"$($script:manifestPath)`""
            $xmlManifest | Should -Exist
            (Get-Content $xmlManifest -Raw) | Should -Match 'packageName="SparseGuideApp"'
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

        It 'Builds the existing sample in Debug mode' -Skip:$script:skip {
            Invoke-Expression 'dotnet build -c Debug'
            $LASTEXITCODE | Should -Be 0
        }

        It 'Packs the checked-in sparse manifest into an identity MSIX' -Skip:$script:skip {
            Invoke-WinappCommand -Arguments 'cert generate --publisher "CN=Sparse App Sample" --if-exists skip'
            'devcert.pfx' | Should -Exist
            Invoke-WinappCommand -Arguments 'pack appxmanifest.xml --cert devcert.pfx'
            'SparseAppSample.identity.msix' | Should -Exist
        }
    }
}
