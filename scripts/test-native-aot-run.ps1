#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs real packaged and unpackaged Native AOT publish-and-run acceptance tests.
.DESCRIPTION
    Copies the packaged WinUI sample and creates a long-running unpackaged console
    fixture in an isolated temporary directory. It gives both unique identities,
    publishes through winapp, launches both apps,
    validates the JSON provenance/verification envelope, then stops and unregisters
    only the resources created by this invocation.

    ARM64 runtime success is tested only when this script runs on Windows ARM64.
.PARAMETER Architecture
    Target/runtime architecture: x64 or arm64. Defaults to the current OS architecture.
.PARAMETER WinappPath
    Path to winapp.exe, or a directory containing winapp.exe / win-<arch>\winapp.exe.
    Defaults to artifacts\cli\win-<arch>\winapp.exe.
.PARAMETER KeepArtifacts
    Keep the isolated temporary fixtures and staging directory for diagnosis.
#>

[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture,
    [string]$WinappPath = "",
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$HostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

if (-not $Architecture) {
    if ($HostArchitecture -notin @("x64", "arm64")) {
        throw "Native AOT acceptance supports x64 and arm64 hosts; current OS architecture is '$HostArchitecture'."
    }
    $Architecture = $HostArchitecture
}

if ($Architecture -eq "arm64" -and $HostArchitecture -ne "arm64") {
    throw "ARM64 runtime acceptance must run on a Windows ARM64 device. This host is '$HostArchitecture'."
}

function Resolve-WinappExecutable {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        $Candidate = Join-Path $ProjectRoot "artifacts\cli\win-$Architecture\winapp.exe"
    }

    if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Candidate).Path
    }

    if (Test-Path -LiteralPath $Candidate -PathType Container) {
        foreach ($relative in @("winapp.exe", "win-$Architecture\winapp.exe")) {
            $resolved = Join-Path $Candidate $relative
            if (Test-Path -LiteralPath $resolved -PathType Leaf) {
                return (Resolve-Path -LiteralPath $resolved).Path
            }
        }
    }

    throw "winapp.exe was not found at '$Candidate'. Build the CLI or pass -WinappPath."
}

function Copy-IsolatedSample {
    param(
        [string]$Source,
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        Where-Object { $_.Name -notin @("bin", "obj", "test.Tests.ps1") } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }
}

function Set-ApplicationManifestIdentity {
    param(
        [string]$Root,
        [string]$UniqueName
    )

    $manifestPath = Join-Path $Root "app.manifest"
    [xml]$document = Get-Content -LiteralPath $manifestPath -Raw
    $assemblyIdentity = $document.assembly.assemblyIdentity
    if ($null -eq $assemblyIdentity) {
        throw "No assemblyIdentity was found in '$manifestPath'."
    }
    $assemblyIdentity.name = "$UniqueName.app"
    $document.Save($manifestPath)
}

function Enable-NativeAotInProject {
    param([string]$ProjectPath)

    [xml]$document = Get-Content -LiteralPath $ProjectPath -Raw
    $propertyGroup = $document.Project.PropertyGroup | Select-Object -First 1
    if ($null -eq $propertyGroup) {
        throw "No PropertyGroup was found in '$ProjectPath'."
    }

    if ($null -eq $propertyGroup.PublishAot) {
        $publishAot = $document.CreateElement("PublishAot")
        $publishAot.InnerText = "true"
        $propertyGroup.AppendChild($publishAot) | Out-Null
    }
    else {
        $propertyGroup.PublishAot = "true"
    }

    $document.Save($ProjectPath)
}

function Invoke-WinappJson {
    param([string[]]$Arguments)

    $output = @(& $script:WinappExecutable @Arguments)
    $exitCode = $LASTEXITCODE
    $text = ($output -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0) {
        throw "winapp exited $exitCode for '$($Arguments -join ' ')'. JSON: $text"
    }

    try {
        return $text | ConvertFrom-Json
    }
    catch {
        throw "winapp returned invalid JSON for '$($Arguments -join ' ')': $text"
    }
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-VerifiedResult {
    param(
        [object]$Result,
        [string]$ExpectedPackaging
    )

    Assert-True ($Result.SchemaVersion -eq 1) "SchemaVersion must be 1."
    Assert-True ($Result.Operation -eq "Publish") "Operation must be Publish."
    Assert-True ($Result.RuntimeIdentifier -eq "win-$Architecture") "RuntimeIdentifier does not match win-$Architecture."
    Assert-True ($Result.Architecture -eq $Architecture) "Architecture does not match $Architecture."
    Assert-True ($Result.PublishAot -eq $true) "PublishAot must be true."
    Assert-True ($Result.Packaging -eq $ExpectedPackaging) "Packaging must be $ExpectedPackaging."
    Assert-True ($Result.ProcessId -gt 0) "A positive ProcessId is required."
    Assert-True ($Result.Alive -eq $true) "The process must survive the verification window."
    Assert-True ($Result.NativeAotVerified -eq $true) "NativeAotVerified must be true."
    Assert-True ($Result.Verification.StaticPayload -eq $true) "Static payload verification must pass."
    Assert-True ($Result.Verification.RuntimeModules -eq $true) "Runtime module verification must pass."
    Assert-True ($Result.Verification.ProcessProvenance -eq $true) "Process provenance must pass."
    Assert-True (Test-Path -LiteralPath $Result.PublishDirectory -PathType Container) "PublishDirectory must exist."
    Assert-True (Test-Path -LiteralPath $Result.SourceExecutable -PathType Leaf) "SourceExecutable must exist."
}

$script:WinappExecutable = Resolve-WinappExecutable $WinappPath
$token = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-native-aot-$Architecture-$token"
$packagedRoot = Join-Path $tempRoot "packaged"
$unpackagedRoot = Join-Path $tempRoot "unpackaged"
$stagingRoot = Join-Path $tempRoot "AppX"
$createdProcessIds = [System.Collections.Generic.List[int]]::new()
$packagedManifest = $null

try {
    Copy-IsolatedSample `
        (Join-Path $ProjectRoot "samples\winui-app") `
        $packagedRoot
    New-Item -ItemType Directory -Path $unpackagedRoot -Force | Out-Null

    $packageIdentity = "WinApp.NativeAot.Acceptance.$token"
    $packagedAssembly = "WinAppNativeAotPackaged$token"
    $unpackagedAssembly = "WinAppNativeAotUnpackaged$token"
    $packagedProject = Join-Path $packagedRoot "$packagedAssembly.csproj"
    Move-Item -LiteralPath (Join-Path $packagedRoot "winui-app.csproj") -Destination $packagedProject
    Enable-NativeAotInProject $packagedProject
    Set-ApplicationManifestIdentity $packagedRoot $packagedAssembly

    $unpackagedProject = Join-Path $unpackagedRoot "$unpackagedAssembly.csproj"
    [System.IO.File]::WriteAllText(
        $unpackagedProject,
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <SelfContained>true</SelfContained>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <AssemblyName>$unpackagedAssembly</AssemblyName>
  </PropertyGroup>
</Project>
"@,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $unpackagedRoot "Program.cs"),
        @"
System.Console.WriteLine("Native AOT acceptance process started.");
System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
"@,
        [System.Text.UTF8Encoding]::new($false))

    $packagedManifest = Join-Path $packagedRoot "Package.appxmanifest"
    [xml]$manifestDocument = Get-Content -LiteralPath $packagedManifest -Raw
    $manifestDocument.Package.Identity.Name = $packageIdentity
    $manifestDocument.Save($packagedManifest)

    $packaged = Invoke-WinappJson @(
        "run", $packagedProject,
        "--verify-native-aot",
        "--detach",
        "-c", "Release",
        "-r", "win-$Architecture",
        "-p", "WindowsAppSDKSelfContained=true",
        "-p", "PublishProfile=",
        "--output-appx-directory", $stagingRoot,
        "--json"
    )
    $createdProcessIds.Add([int]$packaged.ProcessId)
    Assert-VerifiedResult $packaged "Packaged"
    Assert-True ($packaged.PackageIdentity -eq $packageIdentity) "The packaged identity is not invocation-unique."
    Assert-True ($packaged.Verification.PackageRegistration -eq $true) "Package registration provenance must pass."
    Assert-True ([System.IO.Path]::GetFullPath($packaged.StagingDirectory) -eq [System.IO.Path]::GetFullPath($stagingRoot)) "StagingDirectory does not match the selected layout."
    Assert-True ($packaged.MainWindowHandle -gt 0) "The packaged WinUI app must expose a main window."
    Assert-True (-not [string]::IsNullOrWhiteSpace($packaged.MainWindowTitle)) "The packaged WinUI app must expose a window title."

    $unpackaged = Invoke-WinappJson @(
        "run", $unpackagedProject,
        "--verify-native-aot",
        "--detach",
        "-c", "Release",
        "-r", "win-$Architecture",
        "-p", "WindowsAppSDKSelfContained=true",
        "--json"
    )
    $createdProcessIds.Add([int]$unpackaged.ProcessId)
    Assert-VerifiedResult $unpackaged "Unpackaged"
    Assert-True ([string]::IsNullOrWhiteSpace($unpackaged.PackageIdentity)) "Unpackaged launch must not create package identity."
    Assert-True ([System.IO.Path]::GetFullPath($unpackaged.ProcessPath) -eq [System.IO.Path]::GetFullPath($unpackaged.SourceExecutable)) "Unpackaged process must run directly from PublishDir."

    Write-Host "PASS: packaged and unpackaged Native AOT runtime verification succeeded for $Architecture."
}
finally {
    foreach ($processId in $createdProcessIds) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }

    if ($packagedManifest -and (Test-Path -LiteralPath $packagedManifest)) {
        & $script:WinappExecutable unregister --manifest $packagedManifest --force *> $null
    }

    if ($KeepArtifacts) {
        Write-Host "Kept acceptance artifacts at: $tempRoot"
    }
    elseif (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
