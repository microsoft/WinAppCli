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

    # Phase 4 verifies --json stream purity: the analyzer's build warnings must ride the
    # diagnostic stream (stderr), never stdout, so `winapp run --json` output stays
    # machine-parseable. A regression here would silently break scripted / --json consumers
    # the moment a WinUI project has an analyzer finding.
    Context 'Phase 4: WinUI analyzer diagnostics do not pollute --json stdout' {

        BeforeAll {
            if (-not $script:skip) {
                $script:jsonTempDir = New-TempTestDirectory -Prefix 'winui-analyzer-json'

                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        $target = Join-Path $script:jsonTempDir $relative
                        $targetDir = Split-Path $target -Parent
                        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                # Same WUI3001 trigger as Phase 3 (compiles, warns).
                @'
using System;
namespace winui_app;
[AttributeUsage(AttributeTargets.Field)]
internal sealed class ObservablePropertyAttribute : Attribute { }
internal sealed partial class JsonTriggerViewModel { [ObservableProperty] private int _count; }
'@ | Set-Content -Path (Join-Path $script:jsonTempDir 'AnalyzerTrigger.cs')

                Push-Location $script:jsonTempDir
                # Capture stdout (returned) and stderr (redirected) separately. Invoke-WinappCommand
                # returns native stdout; the caller-side 2>$errFile captures native stderr.
                $script:jsonErrFile = Join-Path $script:jsonTempDir 'stderr.txt'
                $script:jsonStdout = (Invoke-WinappCommand -Arguments 'run . --json --no-launch' 2>$script:jsonErrFile) | Out-String
                $script:jsonStderr = if (Test-Path $script:jsonErrFile) { Get-Content $script:jsonErrFile -Raw } else { '' }
                Set-Location $script:originalLocation
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
                try {
                    $manifestPath = Join-Path $script:jsonTempDir 'Package.appxmanifest'
                    if (Test-Path $manifestPath) {
                        [xml]$m = Get-Content -Path $manifestPath -Raw
                        if ($m.Package.Identity.Name) {
                            Get-AppxPackage -Name $m.Package.Identity.Name -ErrorAction SilentlyContinue |
                                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                        }
                    }
                } catch { Write-Warning "Phase 4 cleanup: $_" }
                if (-not $SkipCleanup -and $script:jsonTempDir) { Remove-TempTestDirectory -Path $script:jsonTempDir }
            }
        }

        It 'Emits pure, parseable JSON on stdout' -Skip:$script:skip {
            { $script:jsonStdout | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'Does not leak WUIxxxx warnings onto --json stdout' -Skip:$script:skip {
            $script:jsonStdout | Should -Not -Match 'WUI\d{4}'
        }

        It 'Still surfaces the analyzer warning on stderr' -Skip:$script:skip {
            $script:jsonStderr | Should -Match 'WUI3001'
        }
    }

    # Phase 5 verifies detect-and-skip (design D8): when the project already references the
    # analyzer package itself, winapp must NOT inject a second copy — yet the analyzer must
    # still run (via the user's package). Uses a locally-packed analyzer nupkg so the case is
    # self-contained in CI (no dependency on the package being published yet).
    Context 'Phase 5: winapp skips injection when the analyzer package is already referenced' {

        BeforeDiscovery {
            # Packing + restoring the analyzer package needs dotnet; gate the same way as the suite.
            $script:skipSkipCase = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
        }

        BeforeAll {
            if (-not $script:skip) {
                $script:skipTempDir = New-TempTestDirectory -Prefix 'winui-analyzer-skip'
                $analyzerCsproj = Join-Path $PSScriptRoot '..\..\src\winapp-Analyzer\Microsoft.WindowsAppSDK.Analyzers\Microsoft.WindowsAppSDK.Analyzers.csproj'
                $localFeed = Join-Path $script:skipTempDir '_localfeed'
                New-Item -ItemType Directory -Path $localFeed -Force | Out-Null

                # Pack the in-repo analyzer to a local feed with a deterministic test version.
                dotnet pack $analyzerCsproj -c Release -o $localFeed -p:Version=0.0.0-skiptest --nologo -v quiet | Out-Null

                Get-ChildItem -Path $script:sampleDir -Recurse -File |
                    Where-Object { $_.Name -ne 'test.Tests.ps1' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
                    ForEach-Object {
                        $relative = $_.FullName.Substring($script:sampleDir.Length).TrimStart('\')
                        $target = Join-Path $script:skipTempDir $relative
                        $targetDir = Split-Path $target -Parent
                        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
                        Copy-Item -Path $_.FullName -Destination $target
                    }

                # Add the local feed WITHOUT clearing inherited sources (nuget.org still resolves WindowsAppSDK).
                @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="winui-analyzer-local" value="$localFeed" />
  </packageSources>
</configuration>
"@ | Set-Content -Path (Join-Path $script:skipTempDir 'NuGet.config')

                # The user references the analyzer package themselves → winapp must detect and skip.
                $csproj = Join-Path $script:skipTempDir 'winui-app.csproj'
                (Get-Content $csproj -Raw) -replace '(<PackageReference Include="Microsoft\.WindowsAppSDK"[^/]*/>)', ('$1' + "`n    <PackageReference Include=`"Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer`" Version=`"0.0.0-skiptest`" />") |
                    Set-Content -Path $csproj

                @'
using System;
namespace winui_app;
[AttributeUsage(AttributeTargets.Field)]
internal sealed class ObservablePropertyAttribute : Attribute { }
internal sealed partial class SkipTriggerViewModel { [ObservableProperty] private int _count; }
'@ | Set-Content -Path (Join-Path $script:skipTempDir 'AnalyzerTrigger.cs')

                Push-Location $script:skipTempDir
                # Merged capture (stdout+stderr) so the skip log — emitted under --verbose — is visible.
                $script:skipRunOutput = Invoke-WinappCommand -Arguments 'run . --no-launch --verbose' 2>&1 | Out-String
                Set-Location $script:originalLocation
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
                try {
                    $manifestPath = Join-Path $script:skipTempDir 'Package.appxmanifest'
                    if (Test-Path $manifestPath) {
                        [xml]$m = Get-Content -Path $manifestPath -Raw
                        if ($m.Package.Identity.Name) {
                            Get-AppxPackage -Name $m.Package.Identity.Name -ErrorAction SilentlyContinue |
                                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
                        }
                    }
                } catch { Write-Warning "Phase 5 cleanup: $_" }
                if (-not $SkipCleanup -and $script:skipTempDir) { Remove-TempTestDirectory -Path $script:skipTempDir }
            }
        }

        It 'Logs that it is skipping injection because the package is referenced' -Skip:($script:skip -or $script:skipSkipCase) {
            $script:skipRunOutput | Should -Match 'skipping analyzer injection'
        }

        It 'Does not inject its own analyzer hook' -Skip:($script:skip -or $script:skipSkipCase) {
            # No winapp-injected CustomAfterMicrosoftCommonTargets hook / chained-value token.
            $script:skipRunOutput | Should -Not -Match 'winui-analyzer.*\.props|_WinAppChainedCustomAfter'
        }

        It 'Still surfaces WUI3001 from the user''s own package reference' -Skip:($script:skip -or $script:skipSkipCase) {
            $script:skipRunOutput | Should -Match 'WUI3001'
        }
    }

    # Phase 6 verifies scope control (design M1): a NON-WinUI project gets no analyzer
    # injection and no probe cost. A minimal console app is enough — the cheap UseWinUI text
    # gate short-circuits before any MSBuild evaluation.
    Context 'Phase 6: non-WinUI projects are left alone' {

        BeforeAll {
            if (-not $script:skip) {
                $script:nonWinuiTempDir = New-TempTestDirectory -Prefix 'nonwinui'
                @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $script:nonWinuiTempDir 'ConsoleApp.csproj')
                'System.Console.WriteLine("non-winui ok");' | Set-Content -Path (Join-Path $script:nonWinuiTempDir 'Program.cs')

                Push-Location $script:nonWinuiTempDir
                # Unpackaged console: builds and launches (the app exits immediately).
                $script:nonWinuiOutput = Invoke-WinappCommand -Arguments 'run . --verbose' 2>&1 | Out-String
                Set-Location $script:originalLocation
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Set-Location $script:originalLocation
                if (-not $SkipCleanup -and $script:nonWinuiTempDir) { Remove-TempTestDirectory -Path $script:nonWinuiTempDir }
            }
        }

        It 'Emits no WUIxxxx warnings for a non-WinUI project' -Skip:$script:skip {
            $script:nonWinuiOutput | Should -Not -Match 'WUI\d{4}'
        }

        It 'Does not run the analyzer probe or injection for a non-WinUI project' -Skip:$script:skip {
            $script:nonWinuiOutput | Should -Not -Match 'winui-analyzer|CustomAfterMicrosoftCommonTargets|skipping analyzer injection'
        }
    }

}
