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
    $hasDotnet = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
    # These tests are Windows-specific (manifest gating, MSBuild platform identifiers).
    # On non-Windows hosts (e.g. Linux/macOS CI), skip rather than emit noisy failures.
    $isWindowsHost = if ($null -ne (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue)) { $IsWindows } else { $true }
    $script:skip = (-not $hasDotnet) -or (-not $isWindowsHost)
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
                [string]$ProjectDirManifestName = "",  # 'appxmanifest.xml' | 'Package.appxmanifest' | 'AppxManifest.xml'
                [bool]$ProjectDirManifest = $false,    # convenience: same as -ProjectDirManifestName 'appxmanifest.xml'
                [string]$OutputDirManifestName = "",   # places file at <OutputPath><name>; OutputPath forced to bin\
                [string]$WindowsPackageType = "",
                [string]$CustomManifestPath = "",
                [string]$EnableWinAppRunSupport = "",
                [string]$TargetPlatformIdentifier = ""
            )
            $dir = Join-Path $script:tempRoot $CaseName
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            if ($ProjectDirManifestName) {
                Set-Content -Path (Join-Path $dir $ProjectDirManifestName) -Value '<x/>'
            } elseif ($ProjectDirManifest) {
                Set-Content -Path (Join-Path $dir "appxmanifest.xml") -Value '<x/>'
            }
            if ($CustomManifestPath) {
                $customParent = Join-Path $dir (Split-Path $CustomManifestPath -Parent)
                if ($customParent -and -not (Test-Path $customParent)) {
                    New-Item -ItemType Directory -Path $customParent -Force | Out-Null
                }
                Set-Content -Path (Join-Path $dir $CustomManifestPath) -Value '<x/>'
            }
            if ($OutputDirManifestName) {
                $outDir = Join-Path $dir 'bin'
                New-Item -ItemType Directory -Path $outDir -Force | Out-Null
                Set-Content -Path (Join-Path $outDir $OutputDirManifestName) -Value '<x/>'
            }
            $extraProps = ""
            if ($WindowsPackageType) { $extraProps += "    <WindowsPackageType>$WindowsPackageType</WindowsPackageType>`n" }
            if ($CustomManifestPath) { $extraProps += "    <WinAppManifestPath>`$(MSBuildProjectDirectory)\$CustomManifestPath</WinAppManifestPath>`n" }
            if ($EnableWinAppRunSupport) { $extraProps += "    <EnableWinAppRunSupport>$EnableWinAppRunSupport</EnableWinAppRunSupport>`n" }
            if ($TargetPlatformIdentifier) { $extraProps += "    <TargetPlatformIdentifier>$TargetPlatformIdentifier</TargetPlatformIdentifier>`n" }
            if ($OutputDirManifestName) { $extraProps += "    <OutputPath>bin\</OutputPath>`n" }
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

        It "Activates with project-dir Package.appxmanifest (VS convention)" {
            Get-GateValue -CaseName 'package-appxmanifest' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifestName 'Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates with project-dir AppxManifest.xml (MSBuild output convention)" {
            Get-GateValue -CaseName 'appxmanifest-xml-cap' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -ProjectDirManifestName 'AppxManifest.xml' | Should -Be 'true'
        }

        It "Activates with explicit TargetPlatformIdentifier=windows on a non-windows TFM (non-SDK-style projects)" {
            Get-GateValue -CaseName 'explicit-tpi-windows' -TargetFramework 'net8.0' -OutputType 'Exe' -ProjectDirManifest $true -TargetPlatformIdentifier 'windows' | Should -Be 'true'
        }

        It "Activates with custom WinAppManifestPath pointing to a manifest in a sub-directory" {
            Get-GateValue -CaseName 'custom-mfst-path' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -CustomManifestPath 'platforms\windows\Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates for the Windows TFM of a multi-targeted MAUI-style project" {
            Get-GateValue -CaseName 'maui-windows' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'true'
        }

        It "Activates for MAUI-style head app whose manifest is generated into `$(OutputPath) (Package.appxmanifest)" {
            # MAUI generates the AppxManifest at build time based on platform / msbuild props,
            # so the only manifest that exists lives under bin\ — not in the project directory.
            # The auto-detection in WinAppManifestPath checks $(OutputPath) first; the gate must
            # accept that resolved path, otherwise transitive consumption from MAUI Windows libs
            # never activates and `dotnet run` falls back to the SDK default.
            Get-GateValue -CaseName 'maui-genmfst-pkg' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'WinExe' -OutputDirManifestName 'Package.appxmanifest' | Should -Be 'true'
        }

        It "Activates when the manifest is generated into `$(OutputPath) as AppxManifest.xml" {
            Get-GateValue -CaseName 'output-appxmanifest-xml' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -OutputDirManifestName 'AppxManifest.xml' | Should -Be 'true'
        }

        It "Activates when the manifest is generated into `$(OutputPath) as appxmanifest.xml" {
            Get-GateValue -CaseName 'output-appxmanifest-lower' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'WinExe' -OutputDirManifestName 'appxmanifest.xml' | Should -Be 'true'
        }
    }

    Context "Inactive scenarios - must not fire in transitive consumers" {
        It "Inactive for class libraries (OutputType=Library)" {
            Get-GateValue -CaseName 'lib' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Library' | Should -Be 'false'
        }

        It "Inactive for console apps without a manifest" {
            Get-GateValue -CaseName 'console-no-mfst' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' | Should -Be 'false'
        }

        It "Inactive when WindowsPackageType=MSIX is set but no manifest exists (downstream targets need a real manifest)" {
            # Regression guard: previously the gate accepted WindowsPackageType=MSIX as a
            # standalone activation signal, which let downstream targets proceed without a
            # discoverable manifest and produce hard-to-diagnose failures.
            Get-GateValue -CaseName 'msix-no-mfst' -TargetFramework 'net10.0-windows10.0.19041.0' -OutputType 'Exe' -WindowsPackageType 'MSIX' | Should -Be 'false'
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

        It "Inactive when explicit TargetPlatformIdentifier=android overrides a windows-style TFM" {
            # If a non-SDK-style project explicitly sets TargetPlatformIdentifier, the
            # explicit value must win over what would be derived from the TFM string.
            Get-GateValue -CaseName 'explicit-tpi-android' -TargetFramework 'net8.0-windows10.0.19041.0' -OutputType 'Exe' -ProjectDirManifest $true -TargetPlatformIdentifier 'android' | Should -Be 'false'
        }

        It "Inactive for plain net8.0 (no Windows platform)" {
            Get-GateValue -CaseName 'plain-net8' -TargetFramework 'net8.0' -OutputType 'Exe' -ProjectDirManifest $true | Should -Be 'false'
        }
    }

    Context "Build-time gate re-evaluation - MAUI-style manifest generated during Build" {
        # Regression guard for H1: when no manifest exists at parse time (e.g. MAUI
        # generates one into $(OutputPath) at Build time), the parse-time gate
        # freezes as `false`. `_WinAppResolveManifestPath` runs AfterTargets="Build"
        # and must re-resolve $(WinAppManifestPath) AND recompute $(_WinAppRunSupportActive)
        # so downstream targets (`_WinAppValidateRunSupport`, `_WinAppBuildRunArgs`,
        # `_WinAppPrepareRunArguments`) see the live answer.
        It "Re-activates the gate after Build generates a manifest into `$(OutputPath)" {
            $caseName = 'maui-build-time-mfst'
            $dir = Join-Path $script:tempRoot $caseName
            New-Item -ItemType Directory -Path $dir -Force | Out-Null

            # Need at least one .cs file so the SDK's Build target actually
            # has work to do (otherwise it short-circuits and AfterTargets="Build"
            # targets never fire).
            Set-Content -Path (Join-Path $dir "Program.cs") -Value "class P { static void Main(){} }"

            $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <OutputPath>bin\</OutputPath>
  </PropertyGroup>
  <Import Project="$($script:propsPath)" />
  <Import Project="$($script:targetsPath)" />

  <!--
    Simulate a MAUI-style framework that generates the manifest into
    `$(OutputPath)` during Build (after the SDK targets run, before our
    `_WinAppResolveManifestPath` AfterTargets="Build" target fires).
  -->
  <Target Name="_TestGenerateManifest" AfterTargets="CoreBuild" BeforeTargets="_WinAppResolveManifestPath">
    <MakeDir Directories="`$(OutputPath)" />
    <WriteLinesToFile File="`$(OutputPath)AppxManifest.xml" Lines="&lt;x/&gt;" Overwrite="true" />
  </Target>

  <!--
    Run after _WinAppResolveManifestPath has had a chance to re-evaluate.
    Dump the live property value to a sentinel file so the test can read it
    without parsing MSBuild console output.
  -->
  <Target Name="_TestDumpGateValue" AfterTargets="_WinAppResolveManifestPath">
    <WriteLinesToFile File="gate-value.txt" Lines="`$(_WinAppRunSupportActive)" Overwrite="true" />
  </Target>
</Project>
"@
            Set-Content -Path (Join-Path $dir "test.csproj") -Value $csproj

            Push-Location $dir
            try {
                & dotnet build (Join-Path $dir "test.csproj") -nologo 2>&1 | Out-Null
            } finally {
                Pop-Location
            }

            $gateFile = Join-Path $dir "gate-value.txt"
            $gateFile | Should -Exist -Because "_TestDumpGateValue must have fired after _WinAppResolveManifestPath"
            (Get-Content $gateFile -Raw).Trim() | Should -Be 'true' `
                -Because "_WinAppResolveManifestPath must re-activate the gate once Build has produced the manifest"
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
