param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or
        $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "Node WinUI Sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command node -ErrorAction SilentlyContinue) -or
            $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null
        $script:appDir = $null

        if (-not $script:skip) {
            $script:resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            $script:tempDir = New-TempTestDirectory -Prefix "node-winui"
            $script:appDir = Join-Path $script:tempDir "app"
            $null = New-Item -ItemType Directory -Path $script:appDir -Force
            Get-ChildItem -Path $script:sampleDir -Force |
                Where-Object Name -NotIn @('node_modules', '.winapp', 'package-lock.json') |
                Copy-Item -Destination $script:appDir -Recurse
        }
    }

    AfterAll {
        if (-not $SkipCleanup -and $script:tempDir) {
            Remove-TempTestDirectory -Path $script:tempDir
        }
    }

    Context "Phase 1: Restore generated WinUI bindings" {
        It "Should install the sample and local winapp package" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Install-WinappNpmPackage -PackagePath $script:resolvedPkg
            } finally {
                Pop-Location
            }
        }

        It "Should restore SDK packages and generate bindings" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                Invoke-WinappCommand -Arguments "restore"
            } finally {
                Pop-Location
            }

            $bindings = Join-Path $script:appDir '.winapp\bindings'
            (Join-Path $bindings 'Application.js') | Should -Exist
            (Join-Path $bindings 'AppWindow.js') | Should -Exist
            (Join-Path $bindings 'Border.js') | Should -Exist
            (Join-Path $bindings 'Button.js') | Should -Exist
            (Join-Path $bindings 'ComboBox.js') | Should -Exist
            (Join-Path $bindings 'Grid.js') | Should -Exist
            (Join-Path $bindings 'IMap_Object_Object.js') | Should -Exist
            (Join-Path $bindings 'IVector_UIElement.js') | Should -Exist
            (Join-Path $bindings 'MicaBackdrop.js') | Should -Exist
            (Join-Path $bindings 'PropertyValue.js') | Should -Exist
            (Join-Path $bindings 'ResourceManager.js') | Should -Exist
            (Join-Path $bindings 'SolidColorBrush.js') | Should -Exist
            (Join-Path $bindings 'Window.js') | Should -Exist
            (Join-Path $bindings 'XamlControlsResources.js') | Should -Exist
            (Join-Path $bindings 'XamlControlsXamlMetaDataProvider.js') | Should -Exist
        }

        It "Should project the constructors and Application members used by the sample" -Skip:$script:skip {
            $applicationDts = Get-Content (Join-Path $script:appDir '.winapp\bindings\Application.d.ts') -Raw
            $buttonDts = Get-Content (Join-Path $script:appDir '.winapp\bindings\Button.d.ts') -Raw
            $brushDts = Get-Content (Join-Path $script:appDir '.winapp\bindings\SolidColorBrush.d.ts') -Raw
            $windowDts = Get-Content (Join-Path $script:appDir '.winapp\bindings\Window.d.ts') -Raw
            $applicationDts | Should -Match 'static create\(onLaunched\?: \(\) => void\): Application;'
            $buttonDts | Should -Match 'constructor\(\);'
            $buttonDts | Should -Match 'set content\(value: unknown\)'
            $buttonDts | Should -Match 'onClick\('
            $brushDts | Should -Match 'constructor\(color: Color\);'
            $windowDts | Should -Match 'constructor\(\);'

            $applicationJs = Get-Content (Join-Path $script:appDir '.winapp\bindings\Application.js') -Raw
            $applicationJs | Should -Match 'getWinappsdkResourcePriPath'
            $applicationJs | Should -Match 'onResourceManagerRequested'

        }

        It "Should prepare the exact framework-dependent runtime through the Node SDK" -Skip:$script:skip {
            Push-Location $script:appDir
            try {
                & npm run prepare-runtime
                $LASTEXITCODE | Should -Be 0
            } finally {
                Pop-Location
            }

            $nodeArchitecture = (& node -p 'process.arch').Trim()
            Join-Path $script:appDir ".winapp\runtime\$nodeArchitecture\Microsoft.WindowsAppRuntime.Bootstrap.dll" |
                Should -Exist
        }
    }

    Context "Phase 2: Existing sample source" {
        It "Should contain valid JavaScript" -Skip:$script:skip {
            & node --check (Join-Path $script:sampleDir 'main.js')
            $LASTEXITCODE | Should -Be 0
            & node --check (Join-Path $script:sampleDir 'prepare-runtime.js')
            $LASTEXITCODE | Should -Be 0
            & node --check (Join-Path $script:sampleDir 'winui-worker.js')
            $LASTEXITCODE | Should -Be 0
        }

        It "Should declare unpackaged startup and WinUI binding roots" -Skip:$script:skip {
            $package = Get-Content (Join-Path $script:sampleDir 'package.json') -Raw | ConvertFrom-Json
            $package.scripts.start | Should -Be 'node main.js'
            $package.scripts.'prepare-runtime' | Should -Be 'node prepare-runtime.js'
            $package.imports.'#winapp/bindings'.require | Should -Be './.winapp/bindings/index.js'
            $package.dependencies.'@microsoft/dynwinrt' | Should -Be '0.1.0-preview.15'
            $package.devDependencies.'@microsoft/dynwinrt-codegen' | Should -Be '0.1.0-preview.15'
            $namespaces = $package.winapp.jsBindings.additionalWinmds.namespace
            $namespaces | Should -Contain 'Windows.Foundation'
            $namespaces | Should -Contain 'Microsoft.UI.Xaml'
            $namespaces | Should -Contain 'Microsoft.UI.Xaml.Controls'
            $namespaces | Should -Contain 'Microsoft.UI.Xaml.Media'
            $namespaces | Should -Not -Contain 'Microsoft.UI.Xaml.Hosting'

            (Join-Path $script:sampleDir 'Package.appxmanifest') | Should -Not -Exist
            (Join-Path $script:sampleDir 'run.ps1') | Should -Not -Exist
            (Join-Path $script:sampleDir 'Assets') | Should -Not -Exist
            $mainSource = Get-Content (Join-Path $script:sampleDir 'main.js') -Raw
            $prepareSource = Get-Content (Join-Path $script:sampleDir 'prepare-runtime.js') -Raw
            $workerSource = Get-Content (Join-Path $script:sampleDir 'winui-worker.js') -Raw
            $mainSource |
                Should -Match 'WINAPPSDK_BOOTSTRAP_DLL_PATH'
            $mainSource |
                Should -Match '(?s)initWinappsdk\(2, 2\).*new Worker'
            $prepareSource |
                Should -Match 'runtimePrepare\('
            $prepareSource |
                Should -Match "version: '2\.2\.0'"
            $prepareSource |
                Should -Match 'install: true'
            $workerSource |
                Should -Match "require\('#winapp/bindings'\)"
            $workerSource |
                Should -Not -Match 'initWinappsdk'
            $workerSource |
                Should -Match 'roInitialize\(0\)'
            $workerSource |
                Should -Match 'Application\.create\('
            $workerSource |
                Should -Match 'new Window\(\)'
            $workerSource |
                Should -Match 'new StackPanel\(\)'
            $workerSource |
                Should -Match 'new SolidColorBrush\('
            $workerSource |
                Should -Not -Match '\.createInstance(?:WithColor)?\('
            $workerSource |
                Should -Not -Match '(?:TextBlock|Border)\.create\('
            $workerSource |
                Should -Match 'themePicker\.onSelectionChanged'
            $workerSource |
                Should -Match 'ElementTheme\.Dark'
            $workerSource |
                Should -Match 'TitleBarTheme\.Dark'
            $workerSource |
                Should -Not -Match 'DesktopWindowXamlSource'
        }
    }
}
