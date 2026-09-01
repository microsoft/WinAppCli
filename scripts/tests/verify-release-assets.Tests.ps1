#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/verify-release-assets.ps1.

    These pin the asset-name contract the release depends on. The names are load-bearing: the
    WinGet submission is handed a fixed-length, fixed-order URL list built from them, and the
    documented download links hardcode them.

    Note what is NOT tested here. The renaming itself lives in
    .pipelines/templates/release-assets.yaml, which the Build preflight and Release_GitHub both
    run, so there is exactly one copy and it cannot drift. It is validated by the weekly rehearsal
    executing it against real build output and then running this verifier over the result -
    which is stronger evidence than a unit test over a second copy of the regexes would be.
#>

BeforeAll {
    $script:VerifyScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'verify-release-assets.ps1'

    function New-StagingFixture {
        param(
            [string[]]$Msix = @('winappcli_x64.msix', 'winappcli_arm64.msix'),
            [string[]]$Npm = @('microsoft-winappcli.tgz'),
            [string[]]$NuGet = @('BuildTools.WinApp.nupkg')
        )

        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("verify-" + [guid]::NewGuid().ToString('N'))

        $layout = @{
            'msix-packages' = $Msix
            'npmpackage'    = $Npm
            'nuget-packages' = $NuGet
        }
        foreach ($sub in $layout.Keys) {
            $dir = Join-Path $root $sub
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            foreach ($file in $layout[$sub]) {
                Set-Content -Path (Join-Path $dir $file) -Value 'x'
            }
        }

        return $root
    }
}

Describe 'verify-release-assets.ps1' {

    AfterEach {
        if ($script:root -and (Test-Path $script:root)) {
            Remove-Item $script:root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'passes on a complete, correctly named asset set' {
        $script:root = New-StagingFixture

        { & $script:VerifyScript -StagingPath $script:root } | Should -Not -Throw
    }

    It 'fails when an MSIX architecture is missing' {
        $script:root = New-StagingFixture -Msix @('winappcli_x64.msix')

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when an extra MSIX would shift the wingetcreate URL pairing' {
        $script:root = New-StagingFixture -Msix @(
            'winappcli_x64.msix', 'winappcli_arm64.msix', 'winappcli_x86.msix'
        )

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when an MSIX kept its version, meaning the rename missed' {
        $script:root = New-StagingFixture -Msix @('winappcli_0.6.3.12_x64.msix', 'winappcli_arm64.msix')

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when the npm tarball is missing' {
        $script:root = New-StagingFixture -Npm @()

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when the npm tarball kept its version' {
        $script:root = New-StagingFixture -Npm @('microsoft-winappcli-0.6.3.tgz')

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when no NuGet package was produced' {
        $script:root = New-StagingFixture -NuGet @()

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails when a NuGet package kept its version' {
        $script:root = New-StagingFixture -NuGet @('BuildTools.WinApp.0.6.3.nupkg')

        { & $script:VerifyScript -StagingPath $script:root } |
            Should -Throw -ExpectedMessage '*verification failed*'
    }

    It 'fails fast when the staging path does not exist' {
        $script:root = $null

        { & $script:VerifyScript -StagingPath 'C:\does\not\exist\at\all' } |
            Should -Throw -ExpectedMessage '*does not exist*'
    }
}
