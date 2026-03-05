## When to use

Use this skill when:
- **Generating a development certificate** for local MSIX signing and testing
- **Installing (trusting) a certificate** on a machine so MSIX packages can be installed
- **Signing an MSIX package or executable** for distribution

## Prerequisites

- winapp CLI installed
- **Administrator access** required for `cert install` (trusting certificates on the machine)

## Key concepts

**Publisher matching:** The publisher in your certificate (e.g., `CN=MyCompany`) must exactly match the `Publisher` attribute in `appxmanifest.xml`. Use `--manifest` when generating to auto-match.

**Dev vs. production certs:** `winapp cert generate` creates self-signed certificates for **local testing only**. For production distribution (Microsoft Store or enterprise), obtain a certificate from a trusted Certificate Authority.

**Default password:** Generated certificates use `password` as the default PFX password. Override with `--password`.

## Usage

### Generate a development certificate

```powershell
# Auto-infer publisher from appxmanifest.xml in the current directory
winapp cert generate

# Explicitly point to a manifest
winapp cert generate --manifest ./path/to/appxmanifest.xml

# Set publisher manually (when no manifest exists yet)
winapp cert generate --publisher "CN=Contoso, O=Contoso Ltd, C=US"

# Custom output path and password
winapp cert generate --output ./certs/myapp.pfx --password MySecurePassword

# Custom validity period
winapp cert generate --valid-days 730

# Overwrite existing certificate
winapp cert generate --if-exists overwrite
```

Output: `devcert.pfx` (or custom path via `--output`).

### Install (trust) a certificate

```powershell
# Trust the certificate on this machine (requires admin/elevated terminal)
winapp cert install ./devcert.pfx

# Force reinstall even if already trusted
winapp cert install ./devcert.pfx --force
```

This adds the certificate to the local machine's **Trusted Root Certification Authorities** store. Required before double-clicking MSIX packages or running `Add-AppxPackage`.

### Sign a file

```powershell
# Sign an MSIX package
winapp sign ./myapp.msix ./devcert.pfx

# Sign with custom password
winapp sign ./myapp.msix ./devcert.pfx --password MySecurePassword

# Sign with timestamp for production (signature remains valid after cert expires)
winapp sign ./myapp.msix ./production.pfx --timestamp http://timestamp.digicert.com
```

Note: The `package` command can sign automatically when you pass `--cert`, so you often don't need `sign` separately.

## Recommended workflow

1. **Generate cert** — `winapp cert generate` (auto-infers publisher from manifest)
2. **Trust cert** (one-time) — `winapp cert install ./devcert.pfx` (run as admin)
3. **Package + sign** — `winapp package ./dist --cert ./devcert.pfx`
4. **Distribute** — share the `.msix`; recipients must also trust the cert, or use a trusted CA cert

## Tips

- Always use `--manifest` (or have `appxmanifest.xml` in the working directory) when generating certs to ensure the publisher matches automatically
- For CI/CD, store the PFX as a secret and pass the password via `--password` rather than using the default
- `winapp cert install` modifies the machine certificate store — it persists across reboots and user sessions
- Use `--timestamp` when signing production builds so the signature survives certificate expiration
- You can also use the shorthand: `winapp package ./dist --generate-cert --install-cert` to do everything in one command

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Publisher mismatch" | Cert publisher ≠ manifest publisher | `winapp cert generate --manifest ./appxmanifest.xml` to re-generate with correct publisher |
| "Access denied" / "elevation required" | `cert install` needs admin | Run your terminal as Administrator |
| "Certificate not trusted" | Cert not installed on machine | `winapp cert install ./devcert.pfx` (admin) |
| "Certificate file already exists" | `devcert.pfx` already present | Use `--if-exists overwrite` or `--if-exists skip` |
| Signature invalid after time passes | No timestamp used during signing | Re-sign with `--timestamp http://timestamp.digicert.com` |
