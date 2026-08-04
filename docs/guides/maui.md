<!-- mslearn: true -->
<!-- description: Package and sign a .NET MAUI Windows app with winapp CLI by using the generated resizetizer manifest for reliable MSIX creation. -->
# Using winapp CLI with .NET MAUI (Windows)

This guide focuses on one MAUI-specific pitfall: the source manifest at `Platforms/Windows/Package.appxmanifest` contains `$placeholder$` tokens that `winapp package` does not resolve. For MAUI, package using the generated manifest from `obj\...\resizetizer\m\Package.appxmanifest`.

A concrete MAUI sample project is available at [`samples/maui-app`](../../samples/maui-app).

## Prerequisites

1. .NET SDK + MAUI workload (Windows):
   ```powershell
   winget install Microsoft.DotNet.SDK.10 --source winget
   dotnet workload install maui-windows
   ```
2. winapp CLI:
   ```powershell
   winget install Microsoft.winappcli --source winget
   ```

## 1. Create a MAUI app and publish the Windows head

```powershell
dotnet new maui -n mymauiapp --no-restore
cd mymauiapp

# The stock template targets every MAUI platform. Keep only Windows when
# maui-windows is the only installed workload.
[xml]$project = Get-Content .\mymauiapp.csproj
$propertyGroup = $project.SelectSingleNode("/*[local-name()='Project']/*[local-name()='PropertyGroup'][1]")
$project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='TargetFrameworks']") |
  ForEach-Object { $_.ParentNode.RemoveChild($_) | Out-Null }
$windowsTfm = $project.CreateElement("TargetFrameworks", $project.DocumentElement.NamespaceURI)
$windowsTfm.InnerText = "net10.0-windows10.0.19041.0"
$propertyGroup.PrependChild($windowsTfm) | Out-Null
$project.Save((Resolve-Path .\mymauiapp.csproj).Path)

dotnet publish .\mymauiapp.csproj `
  -c Release `
  -f net10.0-windows10.0.19041.0 `
  -r win-x64 `
  -p:WindowsPackageType=None `
  -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true `
  --output .\publish\win-x64
```

## 2. Package using the generated MAUI manifest

```powershell
$manifest = ".\obj\Release\net10.0-windows10.0.19041.0\win-x64\resizetizer\m\Package.appxmanifest"
if (-not (Test-Path $manifest)) { throw "Resolved manifest not found: $manifest" }

winapp cert generate --manifest $manifest --if-exists overwrite

winapp package .\publish\win-x64 `
  --manifest $manifest `
  --executable mymauiapp.exe `
  --cert .\devcert.pfx
```

## 3. Optional: sign unpackaged binaries

```powershell
winapp sign .\publish\win-x64\mymauiapp.exe .\devcert.pfx --password password
```

## Validation script (dev + CI)

Use the MAUI sample Pester test script:

```powershell
.\scripts\test-samples.ps1 -Samples maui-app
```

This runs `samples/maui-app/test.Tests.ps1`, which creates a MAUI app from scratch, publishes Windows output, validates the generated resizetizer manifest path, packages an MSIX with `winapp`, and signs the unpackaged executable.

The same script is also wired into `.github/workflows/test-samples.yml` under the `maui-app` matrix entry.
