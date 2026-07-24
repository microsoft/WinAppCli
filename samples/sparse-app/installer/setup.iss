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
; Exact package Identity Name (from appxmanifest.xml). Used to unregister precisely
; on uninstall so we don't remove unrelated packages that share a name prefix.
#define MyPackageName "SparseAppSample"
; Path to your published app output (contains sparse-app.exe, Assets\, and the .msix).
; Resolved relative to SourceDir (the sample directory) below.
#define PublishDir "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{7C2E4A1E-9E2B-4C7E-9D1F-0A1B2C3D4E5F}
; The publish output and the identity .msix live in the sample root (the parent of
; this installer\ directory), so resolve all relative [Files] sources from there.
SourceDir=..
OutputDir=installer\Output
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
; The PowerShell arguments are built in [Code] (RegisterParams) so the install path is safely
; escaped for a single-quoted PowerShell literal — an install directory containing a quote
; must not be able to inject additional script.
Filename: "powershell.exe"; \
  Parameters: "{code:RegisterParams}"; \
  StatusMsg: "Registering package identity..."; \
  Flags: runhidden waituntilterminated
; Launch the app after install (optional).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Unregister the sparse package on uninstall (before files are removed).
; MyPackageName is a compile-time constant (no runtime path), so no escaping is required.
; Match the exact Identity Name so we only remove this package, not others sharing a prefix.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name '{#MyPackageName}' | Remove-AppxPackage"""; \
  RunOnceId: "UnregisterSparse"; \
  Flags: runhidden waituntilterminated

[Code]
{ Escapes a value for safe embedding inside a PowerShell single-quoted string literal. }
function EscapePSLiteral(const Value: string): string;
var
  S: string;
begin
  S := Value;
  StringChange(S, '''', '''''');
  Result := S;
end;

{ Builds the full powershell.exe argument string for registering the sparse package,
  escaping the runtime-resolved install directory so it cannot break out of the literal. }
function RegisterParams(Param: string): string;
var
  AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  Result :=
    '-NoProfile -ExecutionPolicy Bypass -Command "Add-AppxPackage -Path ''' +
    EscapePSLiteral(AppDir + '\{#MyMsixName}') +
    ''' -ExternalLocation ''' + EscapePSLiteral(AppDir) + '''"';
end;
