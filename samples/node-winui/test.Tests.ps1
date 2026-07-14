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
                Where-Object Name -NotIn @('node_modules', '.winapp', '.local-node', 'AppX', 'package-lock.json') |
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
            (Join-Path $bindings 'AppWindow.js') | Should -Exist
            (Join-Path $bindings 'Button.js') | Should -Exist
            (Join-Path $bindings 'IVector_UIElement.js') | Should -Exist
            (Join-Path $bindings 'WindowsXamlManager.js') | Should -Exist
        }

        It "Should project inherited Button members used by the sample" -Skip:$script:skip {
            $buttonDts = Get-Content (Join-Path $script:appDir '.winapp\bindings\Button.d.ts') -Raw
            $buttonDts | Should -Match 'set content\(value: unknown\)'
            $buttonDts | Should -Match 'onClick\('
        }
    }

    Context "Phase 2: Existing sample source" {
        It "Should contain valid JavaScript and PowerShell" -Skip:$script:skip {
            & node --check (Join-Path $script:sampleDir 'main.js')
            $LASTEXITCODE | Should -Be 0
            & node --check (Join-Path $script:sampleDir 'winui-worker.js')
            $LASTEXITCODE | Should -Be 0
            [scriptblock]::Create((Get-Content (Join-Path $script:sampleDir 'run.ps1') -Raw)) | Should -Not -BeNullOrEmpty
        }

        It "Should declare the execution alias and WinUI binding roots" -Skip:$script:skip {
            [xml]$manifest = Get-Content (Join-Path $script:sampleDir 'Package.appxmanifest') -Raw
            $manifest.Package.Applications.Application.Extensions.Extension.AppExecutionAlias.ExecutionAlias.Alias |
                Should -Be 'winui-node.exe'

            $package = Get-Content (Join-Path $script:sampleDir 'package.json') -Raw | ConvertFrom-Json
            $package.imports.'#winapp/bindings'.require | Should -Be './.winapp/bindings/index.js'
            $namespaces = $package.winapp.jsBindings.additionalWinmds.namespace
            $namespaces | Should -Contain 'Microsoft.UI.Xaml.Controls'
            $namespaces | Should -Contain 'Microsoft.UI.Xaml.Hosting'

            Get-Content (Join-Path $script:sampleDir 'winui-worker.js') -Raw |
                Should -Match "require\('#winapp/bindings'\)"
            Get-Content (Join-Path $script:sampleDir 'winui-worker.js') -Raw |
                Should -Match 'siteBridge\.moveAndResize'
        }
    }
}
