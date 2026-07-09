## When to use

Use this skill when:
- **Packaging or signing a .NET MAUI Windows app** with winapp (`winapp package` / `winapp sign`)
- **`winapp package` fails** with an error like *"manifest contains unresolved placeholders: `$placeholder$`"*
- **Deciding which manifest to hand to winapp** for a MAUI Windows head project
- **Setting up CI/CD** (GitHub Actions) that builds a MAUI app and produces a signed MSIX and/or signed unpackaged build

MAUI is **not** a "run `winapp init`" framework — the Windows head already has a manifest and a build system that generates the real one for you. The only trick is pointing winapp at the **generated** manifest, never the source one.

## The resizetizer dependency (root cause)

A .NET MAUI project has a **source** manifest at `Platforms/Windows/Package.appxmanifest` that is full of MAUI-specific `$placeholder$` tokens:

```xml
<Identity Name="$placeholder$" Publisher="$placeholder$" Version="$placeholder$" />
<Properties>
  <DisplayName>$placeholder$</DisplayName>
  <PublisherDisplayName>$placeholder$</PublisherDisplayName>
  <Logo>$placeholder$</Logo>
</Properties>
...
<uap:VisualElements DisplayName="$placeholder$" ... Square150x150Logo="$placeholder$" Square44x44Logo="$placeholder$">
```

These are resolved at **build/publish time** by **`Microsoft.Maui.Resizetizer`** (bundled with the MAUI workload), which reads MSBuild properties (`ApplicationTitle`, `ApplicationId`, `ApplicationDisplayVersion`, `ApplicationPublisher`, the `MauiIcon`/`MauiSplashScreen` items, etc.), generates the app icon/tile/splash assets, and writes a **resolved** manifest into the intermediate output.

**Why winapp trips on this:** `winapp package` only auto-resolves its own entry-point tokens — `$targetnametoken$` and `$targetentrypoint$` (via `--executable`). It does **not** understand MAUI's `$placeholder$` tokens. If you point winapp at the raw `Platforms/Windows/Package.appxmanifest`, packaging fails because those placeholders are still literal `$placeholder$` strings.

> **Never edit `Platforms/Windows/Package.appxmanifest` to hard-code values.** The resizetizer overwrites the generated copy on every build, and hand-editing the source breaks the MAUI tooling contract. The fix is to point winapp at the generated manifest instead.

## Where the resolved manifest lives

After a **Windows-targeted build or publish**, MAUI produces two fully-usable resolved manifests:

| Manifest | Path (relative to project) | State |
|----------|----------------------------|-------|
| **Publish-output manifest (preferred)** | `bin\<Config>\<TFM>\<RID>\AppxManifest.xml` | Fully resolved — **no tokens left at all** |
| **Resizetizer manifest** | `obj\<Config>\<TFM>\<RID>\resizetizer\m\Package.appxmanifest` | MAUI `$placeholder$` tokens resolved; `$targetnametoken$`/`$targetentrypoint$` remain (winapp resolves these via `--executable`) |

Where:
- `<Config>` = `Debug` or `Release`
- `<TFM>` = the Windows target framework, e.g. `net10.0-windows10.0.19041.0`
- `<RID>` = `win-x64` or `win-arm64`

Both paths are **per-RID** — you must publish each architecture first, then pack that architecture's manifest.

## Usage

### 1. Publish the Windows head first

The resolved manifest only exists **after** a Windows publish, so always publish before packing:

```powershell
# Self-contained unpackaged publish (no MSIX container) — regenerates the resolved manifest
dotnet publish .\MyApp\MyApp.csproj `
  -c Release `
  -f net10.0-windows10.0.19041.0 `
  -r win-x64 `
  -p:WindowsPackageType=None `
  -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true `
  --output .\publish\win-x64
```

> Multi-targeted MAUI projects (`net10.0-android;net10.0-ios;net10.0-windows10.0.19041.0`) build the Windows head only when you pass the Windows `-f`/`-r`. The winapp MSBuild targets are inert for non-Windows TFMs.

### 2. Package a signed MSIX — point `--manifest` at the resolved manifest

```powershell
$manifest = ".\MyApp\obj\Release\net10.0-windows10.0.19041.0\win-x64\resizetizer\m\Package.appxmanifest"

# Fail fast if the build didn't produce it (usually means you skipped the Windows publish)
if (-not (Test-Path $manifest)) {
    throw "Resolved manifest not found — publish the Windows head first."
}

winapp package .\publish\win-x64 `
  --manifest $manifest `
  --executable MyApp.exe `
  --cert .\devcert.pfx `
  --cert-password $env:SIGN_PFX_PASSWORD `
  --output .\artifacts\MyApp-win-x64.msix
```

`--executable MyApp.exe` resolves the remaining `$targetnametoken$`/`$targetentrypoint$` in the resizetizer manifest.

> **Shortcut:** MAUI also drops a fully-resolved `AppxManifest.xml` into the publish output folder, and `winapp package` auto-detects a manifest in the input folder. So `winapp package .\publish\win-x64 --cert .\devcert.pfx` (no `--manifest`) often works too. Prefer explicit `--manifest` in CI so failures are obvious and never fall back to the source manifest.

### 3. Sign the unpackaged build

For the loose/unpackaged (`WindowsPackageType=None`) build, sign the executables in place:

```powershell
winapp sign .\publish\win-x64\MyApp.exe .\devcert.pfx --password $env:SIGN_PFX_PASSWORD
```

> `winapp sign` uses a **positional** certificate path + `--password`. `winapp package` uses `--cert` / `--cert-password`. Mixing them is a common mistake.

### 4. Publisher must match the certificate

The resolved manifest's `Identity.Publisher` comes from MSBuild (`$(ApplicationPublisher)`, defaulting to something like `CN=User Name`). Your signing certificate subject **must equal** that value exactly, or signing fails with a publisher mismatch.

```powershell
# Read the publisher the resizetizer actually wrote, then generate a matching dev cert
winapp cert generate --manifest $manifest
```

Set `<ApplicationPublisher>CN=Your Company</ApplicationPublisher>` (or the `ApplicationPublisher` MSBuild property) in the `.csproj` to control it, then regenerate the cert to match.

## CI/CD (GitHub Actions)

Build each architecture, then pack its resolved manifest. Store a self-signed (or CA-issued) PFX as a base64 secret.

```yaml
- uses: microsoft/setup-winapp@v1

- name: Restore signing cert
  shell: pwsh
  run: |
    [IO.File]::WriteAllBytes("$env:RUNNER_TEMP\sign.pfx",
      [Convert]::FromBase64String("${{ secrets.SIGN_PFX_BASE64 }}"))
    "SIGN_PFX_PATH=$env:RUNNER_TEMP\sign.pfx" | Out-File $env:GITHUB_ENV -Append

- name: Publish Windows head (x64, self-contained)
  run: >
    dotnet publish .\MyApp\MyApp.csproj -c Release
    -f net10.0-windows10.0.19041.0 -r win-x64
    -p:WindowsPackageType=None -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
    --output .\publish\win-x64

- name: Sign unpackaged binaries (x64)
  shell: pwsh
  run: |
    Get-ChildItem .\publish\win-x64 -Filter *.exe |
      ForEach-Object { winapp sign $_.FullName $env:SIGN_PFX_PATH --password "${{ secrets.SIGN_PFX_PASSWORD }}" --quiet }

- name: Pack signed MSIX (x64)
  shell: pwsh
  run: |
    $manifest = ".\MyApp\obj\Release\net10.0-windows10.0.19041.0\win-x64\resizetizer\m\Package.appxmanifest"
    if (-not (Test-Path $manifest)) { throw "Resolved manifest not found: $manifest" }
    winapp package .\publish\win-x64 --manifest $manifest --executable MyApp.exe `
      --cert $env:SIGN_PFX_PATH --cert-password "${{ secrets.SIGN_PFX_PASSWORD }}" `
      --output .\artifacts\MyApp-win-x64.msix --quiet
```

Repeat the publish + sign + pack steps for `win-arm64` (swap `-r win-arm64` and the `win-arm64` manifest path).

**Tips:**
- Use `-q`/`--quiet` to reduce log noise.
- A **self-signed** cert produces a valid signature but does **not** clear SmartScreen reputation for other users — only an OV/EV cert from a trusted CA builds reputation. See `winapp-signing`.
- Add `devcert.pfx` and decoded PFX paths to `.gitignore`; never commit certificates.

## Tips

- The resolved manifest is **regenerated on every Windows build/publish** — treat `obj\...\resizetizer\m\` and `bin\...\<RID>\AppxManifest.xml` as build outputs, not something to check in.
- If the manifest path doesn't exist, you almost always **forgot to publish the Windows head for that RID** (or targeted a non-Windows TFM). Publish first.
- Package **each architecture separately** from its own per-RID publish folder and manifest, or pass both folders to `winapp package` to build an `.msixbundle` (see `winapp-package`).
- For MSIX that shouldn't require the user to install the Windows App SDK runtime, add `--self-contained` to `winapp package` (or publish with `-p:WindowsAppSDKSelfContained=true` for unpackaged).
- `winapp run`/`dotnet run` on a MAUI Windows head can auto-detect the output manifest, so debugging usually needs no extra flags — the manual `--manifest` matters mainly for explicit `winapp package` in CI.

## Related skills

- **Packaging**: `winapp-package` — full `winapp package` reference, bundles, self-contained
- **Signing**: `winapp-signing` — certificate generation, trust, timestamping, CA vs self-signed
- **Manifest**: `winapp-manifest` — manifest structure and the `$targetnametoken$` placeholder
- **Frameworks**: `winapp-frameworks` — other frameworks (Electron, WPF/WinForms, C++, Rust, Flutter, Tauri)
- Hitting an error? See `winapp-troubleshoot` for the error → solution table

## Troubleshooting

| Error | Cause | Solution |
|-------|-------|----------|
| "manifest contains unresolved placeholders: `$placeholder$`" | Pointed winapp at the **source** `Platforms/Windows/Package.appxmanifest` | Point `--manifest` at the resolved manifest (`obj\...\resizetizer\m\Package.appxmanifest` or `bin\...\<RID>\AppxManifest.xml`) |
| "manifest not found" at the resizetizer path | Windows head not published for that RID | Run `dotnet publish -f <windows-tfm> -r <rid>` **before** packing |
| "unresolved `$targetnametoken$` / `$targetentrypoint$`" | Packed the resizetizer manifest without an entry point | Add `--executable MyApp.exe`, or pack the fully-resolved `bin\...\AppxManifest.xml` instead |
| "Publisher mismatch" during signing | Cert subject ≠ resolved manifest `Identity.Publisher` | `winapp cert generate --manifest <resolved-manifest>`, or set `$(ApplicationPublisher)` in the `.csproj` and regenerate the cert |
| Placeholders reappear after editing the source manifest | Resizetizer overwrites its generated copy each build | Don't hand-edit the source manifest — change the MSBuild properties / `MauiIcon` instead |
