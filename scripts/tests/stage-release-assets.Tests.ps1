#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/stage-release-assets.ps1.

    The renames here decide the public download URLs: the WinGet manifest and the
    installation instructions in the release notes both hardcode winappcli_x64.msix,
    winappcli-x64.zip and microsoft-winappcli.tgz. A regex that stops matching silently
    publishes versioned names instead and breaks the WinGet submission (#568), so these
    tests pin the naming contract the weekly dry run asserts against.
#>

BeforeAll {
    $script:StageScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'stage-release-assets.ps1'

    function New-AssetFixture {
        param(
            [string[]]$Msix = @(),
            [string[]]$Npm = @(),
            [string[]]$NuGet = @()
        )

        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("stage-" + [guid]::NewGuid().ToString('N'))

        foreach ($pair in @(@{ Name = 'msix'; Files = $Msix }, @{ Name = 'npm'; Files = $Npm }, @{ Name = 'nuget'; Files = $NuGet })) {
            $dir = Join-Path $root $pair.Name
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            foreach ($file in $pair.Files) {
                Set-Content -Path (Join-Path $dir $file) -Value 'x'
            }
        }

        return $root
    }

    function Get-AssetNames {
        param([string]$Root, [string]$Sub)
        return @(Get-ChildItem (Join-Path $Root $Sub) -File | Select-Object -ExpandProperty Name | Sort-Object)
    }
}

Describe 'stage-release-assets.ps1' {

    Context 'renaming' {
        BeforeEach {
            $script:root = New-AssetFixture `
                -Msix @('winappcli_0.6.3.12_x64.msix', 'winappcli_0.6.3.12_arm64.msix') `
                -Npm @('microsoft-winappcli-0.6.3.tgz') `
                -NuGet @('BuildTools.WinApp.0.6.3.nupkg')
        }

        AfterEach {
            if ($script:root -and (Test-Path $script:root)) {
                Remove-Item $script:root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'strips the version from MSIX names' {
            & $script:StageScript `
                -MsixPath (Join-Path $script:root 'msix') `
                -NpmPath (Join-Path $script:root 'npm') `
                -NuGetPath (Join-Path $script:root 'nuget')

            Get-AssetNames -Root $script:root -Sub 'msix' |
                Should -Be @('winappcli_arm64.msix', 'winappcli_x64.msix')
        }

        It 'strips the version from the npm tarball' {
            & $script:StageScript -NpmPath (Join-Path $script:root 'npm')

            Get-AssetNames -Root $script:root -Sub 'npm' | Should -Be @('microsoft-winappcli.tgz')
        }

        It 'strips the version from NuGet packages' {
            & $script:StageScript -NuGetPath (Join-Path $script:root 'nuget')

            Get-AssetNames -Root $script:root -Sub 'nuget' | Should -Be @('BuildTools.WinApp.nupkg')
        }

        It 'handles prerelease versions in NuGet package names' {
            $dir = Join-Path $script:root 'nuget'
            Remove-Item (Join-Path $dir '*.nupkg')
            Set-Content -Path (Join-Path $dir 'BuildTools.WinApp.0.6.3-prerelease.12.nupkg') -Value 'x'

            & $script:StageScript -NuGetPath $dir

            Get-AssetNames -Root $script:root -Sub 'nuget' | Should -Be @('BuildTools.WinApp.nupkg')
        }

        It 'is idempotent when assets are already renamed' {
            $msixDir = Join-Path $script:root 'msix'
            & $script:StageScript -MsixPath $msixDir
            { & $script:StageScript -MsixPath $msixDir -Verify } | Should -Not -Throw

            Get-AssetNames -Root $script:root -Sub 'msix' |
                Should -Be @('winappcli_arm64.msix', 'winappcli_x64.msix')
        }
    }

    Context 'verification' {
        AfterEach {
            if ($script:root -and (Test-Path $script:root)) {
                Remove-Item $script:root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'passes when every expected asset is present' {
            $script:root = New-AssetFixture `
                -Msix @('winappcli_0.6.3.12_x64.msix', 'winappcli_0.6.3.12_arm64.msix') `
                -Npm @('microsoft-winappcli-0.6.3.tgz') `
                -NuGet @('BuildTools.WinApp.0.6.3.nupkg')

            {
                & $script:StageScript `
                    -MsixPath (Join-Path $script:root 'msix') `
                    -NpmPath (Join-Path $script:root 'npm') `
                    -NuGetPath (Join-Path $script:root 'nuget') `
                    -Verify
            } | Should -Not -Throw
        }

        It 'fails when an architecture is missing' {
            $script:root = New-AssetFixture -Msix @('winappcli_0.6.3.12_x64.msix')

            { & $script:StageScript -MsixPath (Join-Path $script:root 'msix') -Verify } |
                Should -Throw -ExpectedMessage '*verification failed*'
        }

        It 'fails when an extra MSIX would shift the wingetcreate URL pairing' {
            $script:root = New-AssetFixture -Msix @(
                'winappcli_0.6.3.12_x64.msix',
                'winappcli_0.6.3.12_arm64.msix',
                'winappcli_0.6.3.12_x86.msix'
            )

            { & $script:StageScript -MsixPath (Join-Path $script:root 'msix') -Verify } |
                Should -Throw -ExpectedMessage '*verification failed*'
        }

        It 'fails when the npm tarball is missing' {
            $script:root = New-AssetFixture -Npm @()

            { & $script:StageScript -NpmPath (Join-Path $script:root 'npm') -Verify } |
                Should -Throw -ExpectedMessage '*verification failed*'
        }

        It 'fails when a NuGet package keeps its version' {
            # A hyphen before the version means the rename regex, which expects a dot, misses it.
            $script:root = New-AssetFixture -NuGet @('BuildTools.WinApp-0.6.3.nupkg')

            { & $script:StageScript -NuGetPath (Join-Path $script:root 'nuget') -Verify } |
                Should -Throw -ExpectedMessage '*verification failed*'
        }

        It 'skips verification for directories that were not supplied' {
            $script:root = New-AssetFixture -Msix @('winappcli_0.6.3.12_x64.msix', 'winappcli_0.6.3.12_arm64.msix')

            # Only MSIX is passed, so missing npm and nuget must not fail the run.
            { & $script:StageScript -MsixPath (Join-Path $script:root 'msix') -Verify } | Should -Not -Throw
        }
    }
}
