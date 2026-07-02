; Inno Setup script for the winapp sparse packaging sample.
;
; Demonstrates the full production sparse-packaging flow:
;   build -> package identity MSIX -> install -> register sparse package -> identity available
;
; This installer:
;   1. Installs the WPF app binaries and visual assets to {app}.
;   2. Copies the identity-only .msix alongside the app.
;   3. Registers the sparse package against the install directory (the external
;      content location) as a post-install step.
;   4. Unregisters the package on uninstall.
;
; Prerequisites before compiling with the Inno Setup Compiler (ISCC.exe):
;   - Publish the app:      dotnet publish -c Release -r win-x64 --self-contained false
;   - Build the identity:   winapp pack appxmanifest.xml --cert devcert.pfx
;   - The signing certificate must be trusted on the target machine.
;
; Adjust SourceDir / paths below to match your publish output.

#define MyAppName "Sparse Packaging Sample"
#define MyAppExeName "sparse-app.exe"
#define MyMsixName "SparseAppSample.identity.msix"
; Wildcard matches the package full name (e.g. SparseAppSample_1.0.0.0_neutral__<hash>)
#define MyPackagePattern "SparseAppSample*"
; Path to your published app output (contains sparse-app.exe, Assets\, and the .msix).
#define PublishDir "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{7C2E4A1E-9E2B-4C7E-9D1F-SPARSE0000001}
AppName={#MyAppName}
AppVersion=1.0.0.0
DefaultDirName={autopf}\SparseAppSample
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputBaseFilename=SparseAppSampleSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
; App binaries + assets (everything from the publish output).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs
; The identity-only MSIX, copied alongside the app so it can be registered from {app}.
Source: "{#MyMsixName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Register the sparse identity package against the install directory (external location).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path '{app}\{#MyMsixName}' -ExternalLocation '{app}'"""; \
  StatusMsg: "Registering package identity..."; \
  Flags: runhidden waituntilterminated
; Launch the app after install (optional).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Unregister the sparse package on uninstall (before files are removed).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage '{#MyPackagePattern}' | Remove-AppxPackage"""; \
  RunOnceId: "UnregisterSparse"; \
  Flags: runhidden waituntilterminated
