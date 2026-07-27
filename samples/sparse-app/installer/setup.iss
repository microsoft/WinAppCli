; Inno Setup script for the winapp sparse packaging sample.
;
; Demonstrates the full production sparse-packaging flow:
;   build -> package identity MSIX -> install -> register sparse package -> identity available
;
; This installer:
;   1. Installs the WPF app binaries and visual assets to {app}.
;   2. Copies the identity-only .msix alongside the app.
;   3. Registers the sparse package against the install directory (the external
;      content location) during file installation, so a failure rolls back the install.
;   4. Unregisters the package on uninstall.
;
; Because the app is published framework-dependent (--self-contained false), the target
; machine needs the .NET 10 Desktop Runtime (x64). InitializeSetup checks for it and warns
; before continuing. Publish self-contained instead to remove that runtime dependency.
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
; Install and register per-user (no elevation). Add-AppxPackage registers the sparse
; package for the user who runs it; if the installer were elevated, registration would
; happen for the admin account instead of the installing user. PrivilegesRequired=lowest
; keeps the whole install per-user, so {autopf} resolves to {localappdata}\Programs.
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputBaseFilename=SparseAppSampleSetup
Compression=lzma2
SolidCompression=yes
; Restrict to 64-bit Windows and install in 64-bit mode. The sample publishes only win-x64, so
; ArchitecturesAllowed blocks Setup from running on 32-bit Windows (where it would otherwise
; install in 32-bit mode and leave an app that cannot launch); ArchitecturesInstallIn64BitMode
; selects the native 64-bit install locations on the systems that are allowed.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
; App binaries + assets (everything from the publish output).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs
; The identity-only MSIX, copied alongside the app so it can be registered from {app}. Registration
; runs from this entry's AfterInstall callback (RegisterSparsePackage) — i.e. during the file-copy
; phase, while Inno's automatic rollback is still active — so a registration failure rolls back
; exactly the files this run installed, rather than deleting a pre-existing {app} directory.
Source: "{#MyMsixName}"; DestDir: "{app}"; Flags: ignoreversion; AfterInstall: RegisterSparsePackage

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Launch the app after install (optional). Registration happens in [Code] (RegisterSparsePackage,
; an AfterInstall callback) so a failure aborts setup — with rollback — instead of silently
; completing without identity.
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
  escaping the runtime-resolved install directory so it cannot break out of the literal.
  Add-AppxPackage runs with -ErrorAction Stop inside try/catch so any failure produces a
  nonzero process exit code, which the caller inspects to abort the install.
  -ForceUpdateFromAnyVersion makes re-installs/upgrades a no-op update: registering the same
  1.0.0.0 identity that is already registered (e.g. a repeat install) succeeds by re-pointing the
  external location instead of failing with "package already exists". }
function RegisterParams(Param: string): string;
var
  AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  Result :=
    '-NoProfile -ExecutionPolicy Bypass -Command "try { Add-AppxPackage -Path ''' +
    EscapePSLiteral(AppDir + '\{#MyMsixName}') +
    ''' -ExternalLocation ''' + EscapePSLiteral(AppDir) +
    ''' -ForceUpdateFromAnyVersion -ErrorAction Stop } catch { Write-Error $_; exit 1 }"';
end;

{ Registers the sparse identity package. Invoked as the AfterInstall callback of the .msix [Files]
  entry, so it runs DURING file installation while Inno's automatic rollback is still active: a
  RaiseException here makes Setup roll back exactly the files this run installed (it does NOT touch
  a pre-existing {app} from an earlier install, and it does not run at ssPostInstall where rollback
  is already unavailable). Registration is idempotent (-ForceUpdateFromAnyVersion), so reinstalling
  the same identity is treated as an update rather than a failure. }
procedure RegisterSparsePackage;
var
  ResultCode: Integer;
begin
  if not Exec('powershell.exe', RegisterParams(''), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not start PowerShell to register the sparse identity package. Setup has been rolled back.');
  if ResultCode <> 0 then
    RaiseException('Registering the sparse identity package failed (exit code ' + IntToStr(ResultCode) + '). ' +
      'This app requires package identity, so setup has been aborted and rolled back.');
end;

{ Returns True if a .NET 10 Windows Desktop Runtime is present in the machine-wide shared
  runtime folder. The sample publishes framework-dependent (dotnet publish --self-contained
  false), so without this runtime the WPF app cannot launch even though the package registers. }
function IsDotNetDesktopRuntimeInstalled: Boolean;
var
  RuntimeDir: string;
  FindRec: TFindRec;
begin
  Result := False;
  RuntimeDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(RuntimeDir + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ Prerequisite gate: verify the .NET 10 Desktop Runtime is installed before proceeding. On a
  clean machine the framework-dependent app would install and register but fail to launch, so
  warn with the download link and let the user cancel rather than silently completing. }
function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsDotNetDesktopRuntimeInstalled then
  begin
    if MsgBox('This app is published framework-dependent and requires the .NET 10 Desktop Runtime (x64), '
        + 'which was not detected on this machine.' + #13#10#13#10
        + 'Install it from:' + #13#10
        + 'https://dotnet.microsoft.com/download/dotnet/10.0' + #13#10#13#10
        + 'Continue with setup anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
