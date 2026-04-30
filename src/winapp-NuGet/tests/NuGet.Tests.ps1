#Requires -Modules Pester

<#
.SYNOPSIS
Pester tests for Microsoft.Windows.SDK.BuildTools.WinApp.

.DESCRIPTION
Two suites:

  1. Gate matrix - synthesizes throwaway .csproj files and queries
     $(_WinAppRunSupportActive) via `dotnet msbuild -getProperty:`. Locks in
     the gating logic that keeps the targets safe for transitive consumption
     so they only activate for packaged Windows apps.

  2. Package layout - opens the produced .nupkg and asserts every file in
     build\ has a matching entry in buildTransitive\ (and vice versa).
     WindowsAppSDK uses the same dual-pack pattern; we mirror it.

Run from repo root:

    Invoke-Pester -Path src\winapp-NuGet\tests\NuGet.Tests.ps1
#>

param(
    [string]$NupkgPath
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
}

Describe "Microsoft.Windows.SDK.BuildTools.WinApp gating" -Skip:$script:skip {
    BeforeAll {
        $script:repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
        $script:propsPath = Join-Path $script:repoRoot "src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.props"
        $script:targetsPath = Join-Path $script:repoRoot "src\winapp-NuGet\build\Microsoft.Windows.SDK.BuildTools.WinApp.targets"
        $script:tempRoot = Join-Path ([IO.Path]::GetTempPath()) "winapp-nuget-tests-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:tempRoot -Force | Out-Null

        function script:Get-GateValue {
            param(
                [string]$CaseName,
                [string]$TargetFramework,
                [string]$OutputType,
                [bool]$ProjectDirManifest = $false,
                [string]$WindowsPackageType = "",
                [string]$CustomManifestPath = "",
                [string]$EnableWinAppRunSupport = ""
            )
            $dir = Join-Path $script:tempRoot $CaseName
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            if ($ProjectDirManifest) {
                Set-Content -Path (Join-Path $dir "appxmanifest.xml") -Value '<x/>'
            }
            if ($CustomManifestPath) {
                $customParent = Join-Path $dir (Split-Path $CustomManifestPath -Parent)
                if ($customParent -and -not (Test-Path $customParent)) {
                    New-Item -ItemType Directory -Path $customParent -Force | Out-Null
                }
                Set-Content -Path (Join-Path $dir $CustomManifestPath) -Value '<x/>'
            }
            $extraProps = ""
            if ($WindowsPackageType) { $extraProps += "    <WindowsPackageType>$WindowsPackageType</WindowsPackageType>`n" }
            if ($CustomManifestPath) { $extraProps += "    <WinAppManifestPath>`$(MSBuildProjectDirectory)\$CustomManifestPath</WinAppManifestPath>`n" }
            if ($EnableWinAppRunSupport) { $extraProps += "    <EnableWinAppRunSupport>$EnableWinAppRunSupport</EnableWinAppRunSupport>`n" }
            $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$TargetFramework</TargetFramework>
    <OutputType>$OutputType</OutputType>
$extraProps  </PropertyGroup>
  <Import Project="$($script:propsPath)" />
  <Import Project="$($script:targetsPath)" />
</Project>
"@
            Set-Content -Path (Join-Path $dir "test.csproj") -Value $csproj
            $out = & dotnet msbuild (Join-Path $dir "test.csproj") -getProperty:_WinAppRunSupportActive -nologo 2>&1
            ($out | Select-Object -Last 1).ToString().Trim()
        }
    }

    AfterAll {
        if ($script:tempRoot -and (Test-Path $script:tempRoot)) {
            Remove-Item -Recurse -Force $script:tempRoot -ErrorAction SilentlyContinue
        }
    }

    Context "Active scenarios - packaged Windows apps" {
        It "Activates for WinUI app (OutputType=WinExe, manifest in project dir)" {
            Get-GateValue -CaseName 'winui' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates for packaged console app (OutputType=Exe + manifest) - protects the dotnet-app sample" {
            Get-GateValue -CaseName 'console-pkg' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates with explicit WindowsPackageType=MSIX even without project-dir manifest" {
            Get-GateValue -CaseName 'explicit-msix' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -WindowsPackageType 'MSIX' | Should -Be 'true'
        }

        It "Activates with custom WinAppManifestPath pointing to a manifest in a sub-directory" {
            Get-GateValue -CaseName 'custom-mfst-path' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -CustomManifestPath 'platforms\windows\Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates for the Windows TFM of a multi-targeted MAUI-style project" {
            Get-GateValue -CaseName 'maui-windows' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'true'
        }
    }

    Context "Inactive scenarios - must not fire in transitive consumers" {
        It "Inactive for class libraries (OutputType=Library)" {
            Get-GateValue -CaseName 'lib' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Library' | Should -Be 'false'
        }

        It "Inactive for console apps without a manifest" {
            Get-GateValue -CaseName 'console-no-mfst' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive when WindowsPackageType=None (explicit opt-out, even with manifest)" {
            Get-GateValue -CaseName 'unpkg' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true -WindowsPackageType 'None' | Should -Be 'false'
        }

        It "Inactive when EnableWinAppRunSupport=false (explicit opt-out)" {
            Get-GateValue -CaseName 'opt-out' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifest $true -EnableWinAppRunSupport 'false' | Should -Be 'false'
        }

        It "Inactive for the Android TFM of a MAUI-style project" {
            Get-GateValue -CaseName 'maui-android' -TargetFramework 'net8.0-android' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive for the iOS TFM of a MAUI-style project" {
            Get-GateValue -CaseName 'maui-ios' -TargetFramework 'net8.0-ios' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive for plain net8.0 (no Windows platform)" {
            Get-GateValue -CaseName 'plain-net8' -TargetFramework 'net8.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'false'
        }
    }
}

Describe "Microsoft.Windows.SDK.BuildTools.WinApp package layout" -Skip:$script:skip {
    BeforeAll {
        $script:repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
        if (-not $NupkgPath) {
            $artifactsDir = Join-Path $script:repoRoot "artifacts\nuget"
            if (Test-Path $artifactsDir) {
                $NupkgPath = Get-ChildItem -Path $artifactsDir -Filter "Microsoft.Windows.SDK.BuildTools.WinApp.*.nupkg" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
        }
        $script:nupkg = $NupkgPath
    }

    It "Has been built (artifacts\nuget\Microsoft.Windows.SDK.BuildTools.WinApp.*.nupkg exists)" {
        $script:nupkg | Should -Not -BeNullOrEmpty -Because "Run scripts\build-cli.ps1 to produce the package, or pass -NupkgPath."
        Test-Path $script:nupkg | Should -BeTrue
    }

    It "Mirrors build\ to buildTransitive\ exactly (parity required for transitive flow)" {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($script:nupkg)
        try {
            $build = $z.Entries | Where-Object { $_.FullName -match '^build/' -and -not $_.FullName.EndsWith('/') } |
                ForEach-Object { $_.FullName.Substring('build/'.Length) } | Sort-Object
            $buildTransitive = $z.Entries | Where-Object { $_.FullName -match '^buildTransitive/' -and -not $_.FullName.EndsWith('/') } |
                ForEach-Object { $_.FullName.Substring('buildTransitive/'.Length) } | Sort-Object
        } finally {
            $z.Dispose()
        }
        $build.Count | Should -BeGreaterThan 0
        $buildTransitive.Count | Should -BeGreaterThan 0
        Compare-Object $build $buildTransitive | Should -BeNullOrEmpty -Because "Files in build\ and buildTransitive\ must match exactly so direct and transitive consumers see the same MSBuild logic."
    }
}
