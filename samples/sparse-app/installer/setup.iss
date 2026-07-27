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
; The identity-only MSIX, copied alongside the app so it can be registered from {app}.
Source: "{#MyMsixName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Launch the app after install (optional). Registration happens in [Code] (CurStepChanged)
; so a failure aborts setup instead of silently completing without identity.
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
  nonzero process exit code, which the caller inspects to abort the install. }
function RegisterParams(Param: string): string;
var
  AppDir: string;
begin
  AppDir := ExpandConstant('{app}');
  Result :=
    '-NoProfile -ExecutionPolicy Bypass -Command "try { Add-AppxPackage -Path ''' +
    EscapePSLiteral(AppDir + '\{#MyMsixName}') +
    ''' -ExternalLocation ''' + EscapePSLiteral(AppDir) +
    ''' -ErrorAction Stop } catch { Write-Error $_; exit 1 }"';
end;

{ Registers the sparse identity package as a post-install step. Inno Setup does NOT roll back
  files once installation reaches ssPostInstall (it runs after [Files] has committed), so on a
  registration failure this handler explicitly removes the files it just installed before aborting
  — otherwise the machine would be left with the app installed but without the package identity it
  requires. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  AppDir: string;

  { Best-effort removal of the just-installed payload so a registration failure doesn't leave a
    half-installed app behind. Files are not locked at ssPostInstall (the app hasn't launched
    yet — [Run] executes later), so DelTree can remove them. }
  procedure CleanupInstalledFiles;
  begin
    AppDir := ExpandConstant('{app}');
    if DirExists(AppDir) then
      DelTree(AppDir, True, True, True);
  end;

begin
  if CurStep = ssPostInstall then
  begin
    if not Exec('powershell.exe', RegisterParams(''), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      CleanupInstalledFiles;
      RaiseException('Could not start PowerShell to register the sparse identity package. The partial installation has been removed.');
    end;
    if ResultCode <> 0 then
    begin
      CleanupInstalledFiles;
      RaiseException('Registering the sparse identity package failed (exit code ' + IntToStr(ResultCode) + '). ' +
        'This app requires package identity, so setup has been aborted and the partial installation removed.');
    end;
  end;
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
