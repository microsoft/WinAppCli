<#
.SYNOPSIS
Pester 5.x tests for the cpp-app sample and C++/CMake guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/cpp.md guide from scratch — creates a minimal
  C++ project, runs winapp init, builds with CMake, and packages as MSIX.
Phase 2: Quick build of the existing sample to verify it is not stale.

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
    $script:skip = $null -eq (Get-Command cmake -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)
}

Describe "cpp-app sample" {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:skip = $null -eq (Get-Command cmake -ErrorAction SilentlyContinue) -or $null -eq (Get-Command npm -ErrorAction SilentlyContinue)

        $script:sampleDir = $PSScriptRoot
        $script:tempDir = $null

        if (-not $script:skip) {
            $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
            Install-WinappGlobal -PackagePath $resolvedPkg
        }
    }

    AfterAll {
        if (-not $SkipCleanup) {
            if ($script:tempDir) { Remove-TempTestDirectory -Path $script:tempDir }
            Remove-Item -Path (Join-Path $script:sampleDir "build") -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "Phase 1: C++/CMake Guide Workflow (from scratch)" {
        BeforeAll {
            if (-not $script:skip) {
                $script:tempDir = New-TempTestDirectory -Prefix "cpp-guide"
                Push-Location $script:tempDir

                @'
#include <windows.h>
#include <iostream>
int main() {
    std::cout << "Hello from C++ app" << std::endl;
    return 0;
}
'@ | Set-Content "main.cpp"

                @'
cmake_minimum_required(VERSION 3.20)
project(test-cpp-app LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
add_executable(test-cpp-app main.cpp)
'@ | Set-Content "CMakeLists.txt"
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Pop-Location
            }
        }

        It "winapp init creates config files" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "init . --use-defaults --setup-sdks=stable"
            "winapp.yaml"       | Should -Exist
            "Package.appxmanifest"  | Should -Exist
            ".winapp"           | Should -Exist
        }

        It "adds execution alias to manifest" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "manifest add-alias"
        }

        It "CMake configures successfully" -Skip:$script:skip {
            $output = cmake -B build -DCMAKE_BUILD_TYPE=Debug 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "CMake configure failed: $output"
        }

        It "CMake builds debug successfully" -Skip:$script:skip {
            $output = cmake --build build --config Debug 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "CMake build failed: $output"
        }

        It "runs app with identity via winapp run" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "run build\Debug --unregister-on-exit"
        }

        It "CMake builds release successfully" -Skip:$script:skip {
            $output = cmake --build build --config Release 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "CMake build failed: $output"
        }

        It "generates a dev certificate" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "cert generate --if-exists skip"
            "devcert.pfx" | Should -Exist
        }

        It "packages as MSIX" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "pack build\Release --manifest Package.appxmanifest --cert devcert.pfx"
            Get-ChildItem -Filter "*.msix" | Should -Not -BeNullOrEmpty -Because "MSIX package should be created"
        }
    }

    Context "Phase 2: Sample Build Check" {
        BeforeAll {
            if (-not $script:skip) {
                Push-Location $script:sampleDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Pop-Location
            }
        }

        It "winapp restore succeeds" -Skip:$script:skip {
            Invoke-WinappCommand -Arguments "restore"
        }

        It "sample CMake configures successfully" -Skip:$script:skip {
            $output = cmake -B build -DCMAKE_BUILD_TYPE=Release 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "Sample CMake configure failed: $output"
        }

        It "sample builds and produces cpp-app.exe" -Skip:$script:skip {
            $output = cmake --build build --config Release 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "Sample CMake build failed: $output"
            "build\Release\cpp-app.exe" | Should -Exist
        }
    }
}
