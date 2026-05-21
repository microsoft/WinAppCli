<#
.SYNOPSIS
Pester 5.x tests for the Electron sample and guide workflow.

.DESCRIPTION
Phase 1: Follows the Electron guide from scratch — scaffolds an Electron app,
  installs winapp, initializes workspace, creates and builds C#/C++ addons,
  packages the app, and creates a signed MSIX package.
Phase 2: Quick install of the existing sample to verify it is not stale.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Electron Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:samplePhase2Dir = $null
        $script:appDir = $null
        $script:resolvedPkg = $null

        if (-not $script:skip) {
            $script:resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
        }
    }

    AfterAll {
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            # Phase 2 now runs against a temp copy of samples/electron, so the
            # checked-in sample is never mutated and only the temp copy needs
            # to be torn down — no git checkout / snapshot dance required.
            if ($script:samplePhase2Dir) { Remove-TempTestDirectory -Path $script:samplePhase2Dir }
        }
    }

    Context "Phase 1: Electron Guide Workflow (from scratch)" {
        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix "electron-guide"

                # Use a dedicated npm cache to avoid ECOMPROMISED errors in CI
                $npmCacheDir = Join-Path $script:tempDir ".npm-cache"
                $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
                $env:npm_config_cache = $npmCacheDir
            }
        }

        It "Should create a new Electron app" -Skip:$script:skip {
            Push-Location $script:tempDir
            try {
                $maxRetries = 3
                $created = $false
                for ($i = 1; $i -le $maxRetries; $i++) {
                    if ($i -gt 1) {
                        Remove-Item -Path (Join-Path $script:tempDir "electron-app") -Recurse -Force -ErrorAction SilentlyContinue
                        Invoke-Expression "npm cache clean --force" 2>$null
                        Start-Sleep -Seconds 2
                    }
                    Invoke-Expression "npx -y create-electron-app@latest electron-app"
                    if ($LASTEXITCODE -eq 0) { $created = $true; break }
                }
                $created | Should -Be $true -Because "Electron app creation should succeed within $maxRetries attempts"
                $script:appDir = Join-Path $script:tempDir "electron-app"
                $script:appDir | Should -Exist

                # Electron's postinstall binary download can silently fail on newer Node versions.
                # Explicitly run the install script if the binary is missing.
                $electronExe = Join-Path $script:appDir "node_modules\electron\dist\electron.exe"
                if (-not (Test-Path $electronExe)) {
                    Write-Host "Electron binary not found after scaffold — running explicit install..."
                    Push-Location $script:appDir
                    try {
                        Invoke-Expression "node node_modules/electron/install.js"
                        $electronExe | Should -Exist -Because "Electron binary should be downloaded after explicit install"
                    } finally { Pop-Location }
                }
            } finally { Pop-Location }
        }

        It "Should configure package.json for MSIX" -Skip:$script:skip {
            $pkgPath = Join-Path $script:appDir "package.json"
            $pkg = Get-Content $pkgPath | ConvertFrom-Json
            $pkg | Add-Member -MemberType NoteProperty -Name "displayName" -Value "WinApp Electron Test" -Force
            $pkg | Add-Member -MemberType NoteProperty -Name "description" -Value "Test app for winapp CLI" -Force
            if ([string]::IsNullOrEmpty($pkg.version)) { $pkg.version = "1.0.0" }
            $pkg | ConvertTo-Json -Depth 10 | Set-Content $pkgPath
            $pkgPath | Should -Exist
        }

        It "Should install winapp as a local devDependency" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Install-WinappNpmPackage -PackagePath $script:resolvedPkg
                Join-Path $script:appDir "node_modules\.bin\winapp.cmd" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should initialize winapp workspace with JS bindings and C++ projections" -Skip:$script:skip {
            # `init --use-defaults` invoked via the npm shim auto-answers Yes
            # at the bindings prompt (Add JS/TypeScript bindings? [Y/n]) and
            # runs codegen in one step. C++ projections always run. The
            # prompt only fires when WINAPP_CLI_CALLER=nodejs-package (set by
            # the `npx winapp` shim, which Invoke-WinappCommand resolves to
            # here after Install-WinappNpmPackage). Selecting Yes writes
            # `"winapp": { "jsBindings": {} }` into package.json.
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "init . --use-defaults --setup-sdks=stable"
            } finally { Pop-Location }
        }

        It "Should create workspace files" -Skip:$script:skip {
            Join-Path $script:appDir ".winapp" | Should -Exist
            Join-Path $script:appDir "winapp.yaml" | Should -Exist
            Join-Path $script:appDir "Package.appxmanifest" | Should -Exist
        }

        # ── JS bindings smoke (v2.x) ─────────────────────────────────────
        # Verify the npm-caller init path produced the expected bindings
        # output, lockfile, and runtime dep — and that re-running `restore`
        # is idempotent (no winapp.yaml or package.json mutation).

        It "Should have generated bindings/ with the managed marker" -Skip:$script:skip {
            $bindingsDir = Join-Path $script:appDir "bindings"
            $bindingsDir | Should -Exist
            # Marker proves the staging-then-swap completed.
            (Join-Path $bindingsDir ".dynwinrt-managed") | Should -Exist
            # Full WinAppSDK generates hundreds of .js files; assert a
            # generous lower bound to catch the "0 files generated" regression
            # without being brittle to upstream SDK changes.
            $jsCount = (Get-ChildItem -Path $bindingsDir -Filter '*.js' -ErrorAction SilentlyContinue).Count
            $jsCount | Should -BeGreaterThan 50 -Because "Default jsBindings (full WinAppSDK) should generate many JS files"
        }

        It "Should inject @microsoft/dynwinrt as a runtime dep in package.json" -Skip:$script:skip {
            # Bindings import @microsoft/dynwinrt at load time — must be a
            # production dep so `npm ci --omit=dev` doesn't strip it.
            $pkgPath = Join-Path $script:appDir "package.json"
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $pkg.dependencies.'@microsoft/dynwinrt' | Should -Not -BeNullOrEmpty `
                -Because "init via npm shim with JS bindings must auto-inject the runtime dep"
        }

        It "Should write a winmds.lock.json under .winapp/" -Skip:$script:skip {
            # Seeded by restore (during init); diagnostic record of the
            # winmd → package mapping at codegen time.
            $lockfilePath = Join-Path $script:appDir ".winapp\winmds.lock.json"
            $lockfilePath | Should -Exist
            $lockfile = Get-Content $lockfilePath -Raw | ConvertFrom-Json
            $lockfile.schema | Should -BeGreaterThan 0 -Because "Lockfile should have schema versioning"
            $lockfile.packages | Should -Not -BeNullOrEmpty -Because "Lockfile should record discovered packages"
        }

        It "Should re-run codegen via 'winapp restore' without mutating winapp.yaml or jsBindings" -Skip:$script:skip {
            # `restore` is the read-only re-run path — it must not modify
            # winapp.yaml or the winapp.jsBindings namespace in package.json.
            # Capture both hashes before/after to prove it.
            $yamlPath = Join-Path $script:appDir "winapp.yaml"
            $pkgPath = Join-Path $script:appDir "package.json"
            $bindingsDir = Join-Path $script:appDir "bindings"
            $yamlHashBefore = (Get-FileHash -Path $yamlPath -Algorithm SHA256).Hash
            $pkgHashBefore = (Get-FileHash -Path $pkgPath -Algorithm SHA256).Hash

            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "restore"
            } finally { Pop-Location }

            $yamlHashAfter = (Get-FileHash -Path $yamlPath -Algorithm SHA256).Hash
            $pkgHashAfter = (Get-FileHash -Path $pkgPath -Algorithm SHA256).Hash
            $yamlHashAfter | Should -Be $yamlHashBefore -Because "restore must not mutate winapp.yaml"
            $pkgHashAfter | Should -Be $pkgHashBefore -Because "restore must not mutate package.json (including winapp.jsBindings)"
            (Join-Path $bindingsDir ".dynwinrt-managed") | Should -Exist `
                -Because "restore should leave the managed marker in place after regen"
        }

        It "Should create a C++ native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cpp --name testCppAddon"
                Join-Path $script:appDir "testCppAddon" | Should -Exist
                Join-Path $script:appDir "testCppAddon\binding.gyp" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should create a C# native addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node create-addon --template cs --name testCsAddon"
                Join-Path $script:appDir "testCsAddon" | Should -Exist
                Join-Path $script:appDir "testCsAddon\testCsAddon.csproj" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should build the C++ addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $output = Invoke-Expression "npx node-gyp clean configure build --directory=testCppAddon --verbose 2>&1"
                $output | ForEach-Object { Write-Host $_ }
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should build the C# addon" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-Expression "npm run build-testCsAddon"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should download the Electron binary" -Skip:$script:skip {
            # Electron 42+ stopped auto-downloading on `npm install`; trigger
            # it explicitly. install-electron is the v42 mechanism; exit code
            # is ignored — the Should -Exist below is the real assertion.
            Push-Location $script:appDir
            try {
                & npx --no-install install-electron 2>&1 | ForEach-Object { Write-Host $_ }
                $exe = Join-Path $script:appDir "node_modules\electron\dist\electron.exe"
                $exe | Should -Exist
            } finally { Pop-Location }
        }

        It "Should add Electron debug identity" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "node add-electron-debug-identity --no-install"
            } finally { Pop-Location }
        }

        It "Should package the Electron app" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-Expression "npm run package"
                $LASTEXITCODE | Should -Be 0
                $script:outDir = Join-Path $script:appDir "out"
                $script:outDir | Should -Exist
                $script:appPackageDir = (Get-ChildItem -Path $script:outDir -Directory | Select-Object -First 1).FullName
                $script:appPackageDir | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }

        It "Should register app with winapp run --no-launch" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "run `"$($script:appPackageDir)`" --no-launch"
            } finally { Pop-Location }
        }

        It "Should generate a development certificate" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "cert generate"
                Join-Path $script:appDir "devcert.pfx" | Should -Exist
            } finally { Pop-Location }
        }

        It "Should package as MSIX" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                $certPath = Join-Path $script:appDir "devcert.pfx"
                Invoke-WinappCommand -Arguments "pack `"$($script:appPackageDir)`" --cert `"$certPath`""
                Get-ChildItem -Path $script:appDir -Filter "*.msix" | Should -Not -BeNullOrEmpty
            } finally { Pop-Location }
        }
    }

    Context "Phase 2: Sample Build Check" {
        BeforeAll {
            if (-not $script:skip) {
                # Phase 2 mutates package.json and writes generated artifacts
                # (.winapp, bindings, node_modules, out). To keep the
                # checked-in sample directory pristine for the contributor's
                # working tree, run the entire phase against a temp copy.
                # Exclude only the install/build outputs — keep the tracked
                # package-lock.json so `npm ci` enforces the exact dependency
                # graph contributors ship.
                $script:samplePhase2Dir = New-TempTestDirectory -Prefix "electron-phase2"
                $skipDirs = @('node_modules', '.winapp', 'bindings', 'out')
                Get-ChildItem -Path $script:sampleDir -Force | Where-Object {
                    if ($_.PSIsContainer) { $skipDirs -notcontains $_.Name }
                    else { $true }
                } | ForEach-Object {
                    Copy-Item -Path $_.FullName -Destination $script:samplePhase2Dir -Recurse -Force
                }
            }
        }

        It "Should install sample dependencies" -Skip:$script:skip {
            Push-Location $script:samplePhase2Dir
            try {
                # `npm ci` enforces the tracked package-lock.json so the
                # exact dependency graph contributors ship is exercised
                # (catches transitive regressions a relaxed `npm install`
                # would mask). `--ignore-scripts` skips the sample's
                # `postinstall` (`winapp restore && cert generate &&
                # setup-debug`) which would otherwise call the *published*
                # winappcli pulled in via the devDependency pin — we
                # override it with the local build below before running
                # restore ourselves.
                Invoke-Expression "npm ci --ignore-scripts"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }

        It "Should have node_modules" -Skip:$script:skip {
            Join-Path $script:samplePhase2Dir 'node_modules' | Should -Exist
        }

        It "Should have package.json" -Skip:$script:skip {
            Join-Path $script:samplePhase2Dir 'package.json' | Should -Exist
        }

        It "Should have forge.config.js" -Skip:$script:skip {
            Join-Path $script:samplePhase2Dir 'forge.config.js' | Should -Exist
        }

        It "Should have appxmanifest.xml" -Skip:$script:skip {
            Join-Path $script:samplePhase2Dir 'appxmanifest.xml' | Should -Exist
        }

        It "Should install the locally-built winappcli on top" -Skip:$script:skip {
            # Sample's devDependency pin would otherwise resolve `winapp` to
            # the published version; we need the build under test.
            Push-Location $script:samplePhase2Dir
            try {
                Install-WinappNpmPackage -PackagePath $script:resolvedPkg
                Join-Path $script:samplePhase2Dir 'node_modules\.bin\winapp.cmd' | Should -Exist
            } finally { Pop-Location }
        }

        It "Should exercise the JS bindings flow via 'winapp restore'" -Skip:$script:skip {
            # The sample ships `"winapp": { "jsBindings": {} }` in its
            # package.json; restore must drive the npm-wrapper orchestrator
            # end-to-end (winmd discovery → codegen → runtime-dep injection)
            # so a regression to the bindings pipeline cannot silently
            # survive Phase 2.
            Push-Location $script:samplePhase2Dir
            try {
                Invoke-WinappCommand -Arguments "restore"
            } finally { Pop-Location }
        }

        It "Should have generated bindings/ with the managed marker" -Skip:$script:skip {
            $bindingsDir = Join-Path $script:samplePhase2Dir 'bindings'
            $bindingsDir | Should -Exist
            (Join-Path $bindingsDir '.dynwinrt-managed') | Should -Exist `
                -Because "restore on a workspace with winapp.jsBindings must populate the managed marker"
            $jsCount = (Get-ChildItem -Path $bindingsDir -Filter '*.js' -ErrorAction SilentlyContinue).Count
            $jsCount | Should -BeGreaterThan 50 -Because "Default jsBindings (full WinAppSDK) should generate many JS files"
        }

        It "Should inject @microsoft/dynwinrt as a runtime dep in package.json" -Skip:$script:skip {
            # Bindings import @microsoft/dynwinrt at load time — restore
            # must auto-inject it as a production dep so `npm ci --omit=dev`
            # doesn't strip it.
            $pkgPath = Join-Path $script:samplePhase2Dir 'package.json'
            $pkg = Get-Content $pkgPath -Raw | ConvertFrom-Json
            $pkg.dependencies.'@microsoft/dynwinrt' | Should -Not -BeNullOrEmpty `
                -Because "restore on a workspace with winapp.jsBindings must inject the runtime dep"
        }

        It "Should materialize @microsoft/dynwinrt under node_modules after a follow-up install" -Skip:$script:skip {
            # The injector only mutates package.json; a follow-up `npm install`
            # is what actually pulls the runtime down. Re-install with
            # --ignore-scripts so we don't recurse through `winapp restore`
            # again, then assert the package is on disk — guarantees the
            # runtime is actually resolvable at app start, not just declared.
            Push-Location $script:samplePhase2Dir
            try {
                Invoke-Expression "npm install --ignore-scripts"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
            Join-Path $script:samplePhase2Dir 'node_modules\@microsoft\dynwinrt' | Should -Exist `
                -Because "the runtime dep injected by restore must actually be installable"
        }

        It "Should run the full sample build (build-all)" -Skip:$script:skip {
            # `build-all = build-csAddon && build-addon` — the full build the
            # sample's package, package-msix, and package-msix:x64 scripts
            # depend on. Building only `build-csAddon` (the previous
            # assertion) leaves the C++ side untested and let a node-gyp /
            # node-addon-api regression survive Phase 2.
            Push-Location $script:samplePhase2Dir
            try {
                Invoke-Expression "npm run build-all"
                $LASTEXITCODE | Should -Be 0
            } finally { Pop-Location }
        }
    }
}
