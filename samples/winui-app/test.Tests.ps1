param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe 'winui-app sample' {

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

        # Phase 1 registers a loose-layout dev package (winapp run . --no-launch). Unregister it
        # before the backing files are deleted so we don't leave a dangling registration on the
        # machine / CI runner. Best-effort: read the identity Name from the manifest and
        # remove any matching registered package.
        try {
            $manifestPath = Join-Path $script:sampleDir 'Package.appxmanifest'
            if (Test-Path $manifestPath) {
                [xml]$manifestXml = Get-Content -Path $manifestPath -Raw
                $identityName = $manifestXml.Package.Identity.Name
                if ($identityName) {
                    Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue |
                        ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                }
            }
        } catch {
            Write-Warning "winui-app cleanup: failed to unregister dev package: $_"
        }

        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Phase 1 exercises `winapp run` PROJECT MODE against a packaged WinUI app from a
    # clean directory: build + property resolution -> packaged detection -> loose-layout
    # registration (identity) WITHOUT launching (deterministic, no GUI required).
    Context 'Phase 1: Project-mode run (from scratch)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix 'winui-packaged'

                # Copy the sample sources (never bin/obj) into a clean temp project dir.
                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        $target = Join-Path $script:tempDir $relative
                        $targetDir = Split-Path $target -Parent
                        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                Push-Location $script:tempDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
            }
        }

        It 'Detects the project as packaged (WindowsPackageType != None)' -Skip:$script:skip {
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
            $wpt = dotnet build 'winui-app.csproj' -t:Build -c Debug -r $rid --getProperty:WindowsPackageType
            $LASTEXITCODE | Should -Be 0
            "$wpt".Trim() | Should -Not -Be 'None'
        }

        It 'Builds and registers the packaged app with winapp run . --no-launch' -Skip:$script:skip {
            # --no-launch builds the loose layout and registers a debug identity
            # without launching the app (no GUI, deterministic in CI).
            Invoke-WinappCommand -Arguments 'run . --no-launch'
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
            dotnet restore
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds existing sample in Debug mode' -Skip:$script:skip {
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
            dotnet build -c Debug -r $rid
            $LASTEXITCODE | Should -Be 0
        }
    }

    # Phase 3 exercises the WinUI analyzer injection (issue #634): a WinUI project
    # built through `winapp run` should surface the analyzer's WUIxxxx warnings in
    # the build output, WITHOUT winapp modifying any user-owned file. Uses a fresh
    # temp copy seeded with a deterministic analyzer trigger (a field-backed
    # [ObservableProperty], which the WUI3001 rule flags on code that still compiles).
    # This is the first test that verifies the real end-to-end behavior (a genuine
    # Roslyn compile) rather than the CLI's argument plumbing.
    Context 'Phase 3: WinUI analyzer diagnostics (winapp run)' {

        BeforeAll {
            if (-not $script:skip) {
                $script:analyzerTempDir = New-TempTestDirectory -Prefix 'winui-analyzer'

                # Copy the sample sources (never bin/obj/test) into a clean temp project dir.
                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        $target = Join-Path $script:analyzerTempDir $relative
                        $targetDir = Split-Path $target -Parent
                        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                # Seed a deterministic analyzer trigger that COMPILES cleanly but still
                # warns: a field-backed [ObservableProperty], which WUI3001 flags. The
                # attribute is defined locally so the trigger needs no MVVM/WinUI package
                # (WUI3001 matches the attribute name syntactically), and — unlike a UWP
                # namespace/API trigger — it does not produce a C# compile error that would
                # fail the build instead of warning.
                $trigger = @'
// Intentional analyzer trigger for the WinUI analyzer integration test (WUI3001).
// Field-backed [ObservableProperty] is flagged; the attribute is defined locally so
// this compiles with no extra package reference.
using System;

namespace winui_app;

[AttributeUsage(AttributeTargets.Field)]
internal sealed class ObservablePropertyAttribute : Attribute { }

internal sealed partial class AnalyzerTriggerViewModel
{
    [ObservableProperty]
    private int _count;
}
'@
                Set-Content -Path (Join-Path $script:analyzerTempDir 'AnalyzerTrigger.cs') -Value $trigger

                # Capture a byte-hash snapshot of every source file so we can assert
                # winapp did not mutate any user-owned file during the build.
                $script:preRunHashes = @{}
                Get-ChildItem -Path $script:analyzerTempDir -Recurse -File |
                    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $rel = $_.FullName.Substring($script:analyzerTempDir.Length)
                        $script:preRunHashes[$rel] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
                    }

                Push-Location $script:analyzerTempDir
                # Build (and register the loose layout) without launching; capture all output.
                $script:analyzerRunOutput = Invoke-WinappCommand -Arguments 'run . --no-launch' 2>&1 | Out-String
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation

                # Unregister the dev package this Phase registered (identity name from the manifest).
                try {
                    $manifestPath = Join-Path $script:analyzerTempDir 'Package.appxmanifest'
                    if (Test-Path $manifestPath) {
                        [xml]$manifestXml = Get-Content -Path $manifestPath -Raw
                        $identityName = $manifestXml.Package.Identity.Name
                        if ($identityName) {
                            Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue |
                                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                        }
                    }
                } catch {
                    Write-Warning "Phase 3 cleanup: failed to unregister dev package: $_"
                }

                if (-not $SkipCleanup -and $script:analyzerTempDir) {
                    Remove-TempTestDirectory -Path $script:analyzerTempDir
                }
            }
        }

        It 'Surfaces a WinUI analyzer warning (WUIxxxx) during winapp run' -Skip:$script:skip {
            # The analyzer runs at CoreCompile, so the warning appears in the streamed build output.
            $script:analyzerRunOutput | Should -Match 'WUI\d{4}'
        }

        It 'Flags the seeded field-backed [ObservableProperty] as WUI3001' -Skip:$script:skip {
            $script:analyzerRunOutput | Should -Match 'WUI3001'
        }

        It 'Does not create a Directory.Build.props in the project' -Skip:$script:skip {
            # The injection uses a build-only -p: hook in the winapp cache, never a file
            # written into the user's project (unlike the old helper-script approach).
            Test-Path (Join-Path $script:analyzerTempDir 'Directory.Build.props') | Should -BeFalse
        }

        It 'Does not mutate any user-owned source file' -Skip:$script:skip {
            foreach ($rel in $script:preRunHashes.Keys) {
                $path = Join-Path $script:analyzerTempDir $rel
                Test-Path $path | Should -BeTrue -Because "winapp must not delete $rel"
                (Get-FileHash $path -Algorithm SHA256).Hash |
                    Should -Be $script:preRunHashes[$rel] -Because "winapp must not modify $rel"
            }
        }
    }

}
