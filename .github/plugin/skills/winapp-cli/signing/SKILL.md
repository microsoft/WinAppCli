---
name: winapp-signing
description: Create and manage code signing certificates for Windows apps and MSIX packages. Use when generating a certificate, signing a Windows app or installer, or fixing certificate trust issues.
version: 0.2.1
---
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


## Command Reference

### `winapp cert generate`

Create a self-signed certificate for local testing only. Publisher must match AppxManifest.xml (auto-inferred if --manifest provided or appxmanifest.xml is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--export-cer` | Export a .cer file (public key only) alongside the .pfx | (none) |
| `--if-exists` | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) | `Error` |
| `--install` | Install the certificate to the local machine store after generation | (none) |
| `--json` | Format output as JSON | (none) |
| `--manifest` | Path to appxmanifest.xml file to extract publisher information from | (none) |
| `--output` | Output path for the generated PFX file | (none) |
| `--password` | Password for the generated PFX file | `password` |
| `--publisher` | Publisher name for the generated certificate. If not specified, will be inferred from manifest. | (none) |
| `--valid-days` | Number of days the certificate is valid | `365` |

### `winapp cert install`

Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<cert-path>` | Yes | Path to the certificate file (PFX or CER) |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--force` | Force installation even if the certificate already exists | (none) |
| `--password` | Password for the PFX file | `password` |

### `winapp sign`

Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<file-path>` | Yes | Path to the file/package to sign |
| `<cert-path>` | Yes | Path to the certificate file (PFX format) |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--password` | Certificate password | `password` |
| `--timestamp` | Timestamp server URL | (none) |
