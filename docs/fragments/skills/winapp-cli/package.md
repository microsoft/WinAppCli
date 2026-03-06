## When to use

Use this skill when:
- **Creating an MSIX installer** from a built app for distribution or testing
- **Packaging any Windows app** — GUI apps, console apps, CLI tools, services, or background processes
- **Signing a package** with a development or production certificate
- **Bundling the Windows App SDK runtime** for self-contained deployment

## Prerequisites

Before packaging, you need:
1. **Built app output** in a folder (e.g., `bin/Release/`, `dist/`, `build/`)
2. **`appxmanifest.xml`** — from `winapp init` or `winapp manifest generate`
3. **Certificate** (optional) — `devcert.pfx` from `winapp cert generate` for signing

## Usage

### Basic packaging (unsigned)

```powershell
# Package from build output — manifest auto-detected from current dir or input folder
winapp package ./bin/Release

# Specify manifest location explicitly
winapp package ./dist --manifest ./appxmanifest.xml
```

### Package and sign in one step

```powershell
# Sign with existing certificate
winapp package ./bin/Release --cert ./devcert.pfx

# Custom certificate password
winapp package ./bin/Release --cert ./devcert.pfx --cert-password MyP@ssw0rd
```

### Generate certificate + package in one step

```powershell
# Auto-generate cert, sign, and package
winapp package ./bin/Release --generate-cert

# Also install the cert to trust it on this machine (requires admin)
winapp package ./bin/Release --generate-cert --install-cert
```

### Self-contained deployment

```powershell
# Bundle Windows App SDK runtime so users don't need it installed (must have winappsdk reference in the winapp.yaml or *.csproj)
winapp package ./bin/Release --cert ./devcert.pfx --self-contained
```

### Custom output path and name

```powershell
# Specify output file
winapp package ./dist --output ./releases/myapp-v1.0.msix --cert ./devcert.pfx

# Custom package name
winapp package ./dist --name "MyApp_1.0.0_x64" --cert ./devcert.pfx
```

## What the command does

1. **Locates `appxmanifest.xml`** — looks in input folder, then current directory (or uses `--manifest`)
2. **Copies manifest + assets** into a staging layout alongside your app files
3. **Generates `resources.pri`** — Package Resource Index for UWP-style resource lookup (skip with `--skip-pri`)
4. **Runs `makeappx pack`** — creates the `.msix` package file
5. **Signs the package** (if `--cert` provided) — calls `signtool` with your certificate

Output: a `.msix` file that can be installed on Windows via double-click or `Add-AppxPackage`.

## Installing the MSIX for testing

```powershell
# Trust the dev certificate first (one-time, requires admin)
winapp cert install ./devcert.pfx

# Install the MSIX
Add-AppxPackage ./myapp.msix

# Uninstall if needed
Get-AppxPackage *myapp* | Remove-AppxPackage
```

## Recommended workflow

1. **Build** your app (`dotnet build`, `cmake --build`, `npm run make`, etc.)
2. **Package** — `winapp package <build-output> --cert ./devcert.pfx`
3. **Trust cert** (first time) — `winapp cert install ./devcert.pfx` (admin)
4. **Install** — double-click the `.msix` or `Add-AppxPackage ./myapp.msix`
5. **Test** the installed app from the Start menu

### Advanced: External content catalog

For sparse packages with `AllowExternalContent`, you may need a code integrity catalog:

```powershell
# Generate CodeIntegrityExternal.cat for external executables
winapp create-external-catalog "./bin/Release"

# Include subdirectories and specify output path
winapp create-external-catalog "./bin/Release" --recursive --output ./catalog/CodeIntegrityExternal.cat
```

This hashes executables in the specified directories so Windows trusts them when running with sparse package identity.

## CI/CD

### GitHub Actions

Use the `microsoft/setup-winapp` action to install winapp on GitHub-hosted runners:

```yaml
- uses: microsoft/setup-winapp@v1

- name: Package
  run: winapp package ./dist --cert ${{ secrets.CERT_PATH }} --cert-password ${{ secrets.CERT_PASSWORD }} --quiet
```

**Tips for CI/CD pipelines:**
- Use `--quiet` (or `-q`) to suppress progress output
- Use `--if-exists skip` with `winapp cert generate` to avoid regenerating existing certificates
- Store your PFX certificate as a repository secret and decode it in CI
- Use `--use-defaults` (or `--no-prompt`) with `winapp init` to avoid interactive prompts

## Tips

- The `package` command aliases to `pack` — both work identically
- `appxmanifest.xml` Publisher must match the certificate publisher — use `winapp cert generate --manifest` to ensure they match
- Use `--skip-pri` if your app doesn't use Windows resource loading (e.g., most Electron/Rust/C++ apps without UWP resources)
- For framework-specific packaging paths (Electron, .NET, Rust, etc.), see the `winapp-frameworks` skill
- The `--executable` flag overrides the entry point in the manifest — useful when your exe name differs from what's in `appxmanifest.xml`
- For production distribution, use a certificate from a trusted CA and add `--timestamp` when signing with `winapp sign`

## Related skills
- Need a manifest first? See `winapp-manifest` to generate `appxmanifest.xml`
- Need a certificate? See `winapp-signing` for certificate generation and management
- Having issues? See `winapp-troubleshoot` for a command selection flowchart and error solutions

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "appxmanifest.xml not found" | No manifest in input folder or current dir | Run `winapp init` or `winapp manifest generate` first |
| "Publisher mismatch" | Cert publisher ≠ manifest publisher | Regenerate cert with `winapp cert generate --manifest`, or edit manifest |
| "Package installation failed" | Cert not trusted or stale package | Run `winapp cert install ./devcert.pfx` (admin), then `Get-AppxPackage <name> \| Remove-AppxPackage` |
| "makeappx not found" | Build tools not downloaded | Run `winapp update` or `winapp tool makeappx --help` to trigger download |
