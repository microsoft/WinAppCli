---
name: winapp-signing
description: Create and manage code signing certificates for Windows apps and MSIX packages. Use when generating a certificate, signing a Windows app or installer, or fixing certificate trust issues.
version: 0.5.1
---
## When to use

Use this skill when:
- **Generating a development certificate** for local MSIX signing and testing
- **Installing (trusting) a certificate** on a machine so MSIX packages can be installed
- **Signing an MSIX package or executable** for distribution
- **Signing with Azure Trusted Signing** (cloud-managed signing identity) via `winapp az-sign`

## Prerequisites

- winapp CLI installed
- **Administrator access** required for `cert install` (trusting certificates on the machine)

## Key concepts

**Publisher matching:** The publisher in your certificate must exactly match the `Publisher` attribute in `Package.appxmanifest`. Any valid X.500 distinguished name is supported (e.g., `CN=MyCompany` or `OU=Team, O=Corp, C=US`). Use `--manifest` when generating to auto-match.

**Dev vs. production certs:** `winapp cert generate` creates self-signed certificates for **local testing only**. For production distribution (Microsoft Store or enterprise), obtain a certificate from a trusted Certificate Authority.

**Default password:** Generated certificates use `password` as the default PFX password. Override with `--password`.

## Usage

### Generate a development certificate

```powershell
# Auto-infer publisher from Package.appxmanifest in the current directory
winapp cert generate

# Explicitly point to a manifest
winapp cert generate --manifest ./path/to/Package.appxmanifest

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

### Bundle signing

When packaging multiple architectures into an `.msixbundle`, only the bundle needs to be signed — the signature covers all packages inside. The individual `.msix` slices do not need separate signatures.

Note: The `package` command can sign automatically when you pass `--cert`, so you often don't need `sign` separately.

### Sign with Azure Trusted Signing (cloud signing)

For production-grade signing without managing a PFX file, use `winapp az-sign` to sign with [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/). The signing identity (certificate) is managed in Azure, so no private key ever lives on the machine.

```powershell
# Interactive: discover subscription, account, and profile (prompts for any not provided)
winapp az-sign ./app.msix

# Fully specified — no prompting (ideal for CI/CD)
winapp az-sign ./app.msix --subscription <sub-id> --resource-group <rg> --account <account> --profile <profile>

# Reuse an existing metadata.json (skips all discovery/prompting)
winapp az-sign ./app.msix --metadata-file ./metadata.json
```

**Authentication:** `az-sign` uses Azure's standard credential chain (`DefaultAzureCredential`). In CI/CD, set `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET` (or GitHub Actions OIDC / managed identity). Interactively, if no credentials are found and the Azure CLI is installed, `az-sign` runs `az login` for you.

**Prerequisites:** An Azure Code Signing account and certificate profile (created in the Azure portal after identity validation), plus a role assignment granting your identity the **Code Signing Certificate Profile Signer** role. Signing also requires two machine-wide x64 runtimes that winapp does **not** install for you (it downloads the raw NuGet signing package, not the official client-tools installer): the **x64 .NET 8+ runtime** (the signing library is a managed assembly loaded by `signtool.exe`; winapp's self-contained runtime does not satisfy it) and the **x64 Visual C++ Redistributable** (https://aka.ms/vs/17/release/vc_redist.x64.exe). Also requires **SignTool 10.0.22621.755 or later**. If signing fails while *loading* the dlib (e.g. `0xc000007b` or a missing-DLL error) rather than during authentication, install the missing runtime — most often the VC++ Redistributable.

> **Least-privilege CI:** Auto-discovery (listing subscriptions, resource groups, accounts, and profiles) needs read access at a parent scope. To avoid *every* collection-listing call, pass all four of `--subscription`, `--resource-group`, `--account`, and `--profile`: `az-sign` then validates the account and profile with direct resource reads (a GET on each named resource) instead of enumerating the parent collection, so a principal scoped to just that account and profile is sufficient. Omitting any one of them re-introduces a listing call — for example, leaving out `--subscription` makes `az-sign` list the subscriptions your identity can access — which a narrowly-scoped principal may not be permitted to do. A principal scoped only to a single certificate profile can skip validation entirely by passing a pre-generated `--metadata-file` (which specifies the account endpoint and profile directly).

## Recommended workflow

1. **Generate cert** — `winapp cert generate` (auto-infers publisher from manifest)
2. **Trust cert** (one-time) — `winapp cert install ./devcert.pfx` (run as admin)
3. **Package + sign** — `winapp package ./dist --cert ./devcert.pfx`
4. **Distribute** — share the `.msix`; recipients must also trust the cert, or use a trusted CA cert

## Tips

- Always use `--manifest` (or have `Package.appxmanifest` in the working directory) when generating certs to ensure the publisher matches automatically
- For CI/CD, store the PFX as a secret and pass the password via `--password` rather than using the default
- `winapp cert install` modifies the machine certificate store — it persists across reboots and user sessions
- Use `--timestamp` when signing production builds so the signature survives certificate expiration
- You can also use the shorthand: `winapp package ./dist --generate-cert --install-cert` to do everything in one command

## Related skills
- Need to create a manifest first? See `winapp-manifest` to generate `Package.appxmanifest` with correct publisher info
- Ready to package? See `winapp-package` to create and sign an MSIX in one step
- Having issues? See `winapp-troubleshoot` for common error solutions

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Publisher mismatch" | Cert publisher ≠ manifest publisher | `winapp cert generate --manifest ./Package.appxmanifest` to re-generate with correct publisher |
| "Access denied" / "elevation required" | `cert install` needs admin | Run your terminal as Administrator |
| "Certificate not trusted" | Cert not installed on machine | `winapp cert install ./devcert.pfx` (admin) |
| "Certificate file already exists" | `devcert.pfx` already present | Use `--if-exists overwrite` or `--if-exists skip` |
| Signature invalid after time passes | No timestamp used during signing | Re-sign with `--timestamp http://timestamp.digicert.com` |
| `az-sign` fails with "No credentials found" | No Azure auth in environment | Run `az login`, or set `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` for CI/CD |
| `az-sign` "No Trusted Signing accounts found" | No account in the subscription/resource group | Create a Trusted Signing account and certificate profile in the Azure portal |


## Command Reference

### `winapp cert generate`

Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--export-cer` | Export a .cer file (public key only) alongside the .pfx | (none) |
| `--if-exists` | Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) | `Error` |
| `--install` | Install the certificate to the local machine store after generation | (none) |
| `--json` | Format output as JSON | (none) |
| `--manifest` | Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from | (none) |
| `--output` | Output path for the generated PFX file | (none) |
| `--password` | Password for the generated PFX file | `password` |
| `--publisher` | Publisher distinguished name (DN) for the generated certificate (e.g., CN=MyCompany or OU=Team, O=Corp, C=US). If not specified, will be inferred from manifest. Bare names are auto-wrapped as CN=<name>. | (none) |
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

### `winapp cert info`

Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<cert-path>` | Yes | Path to the certificate file (PFX) |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
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

### `winapp az-sign`

Code-sign a file using Azure Trusted Signing. Signs executables, MSIX packages, or MSIX bundles using a cloud-managed signing identity. Example: winapp az-sign ./app.msix

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<file-path>` | Yes | Path to the file to sign (exe, msix, or msixbundle) |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--account` | Signing account name. Must be used with --resource-group | (none) |
| `--metadata-file` | Path to an existing metadata.json file. Skips all prompting and uses this file directly for signing. | (none) |
| `--profile` | Certificate profile name. Must be used with --account | (none) |
| `--resource-group` | Resource group to narrow down signing accounts | (none) |
| `--subscription` | Azure subscription ID to use. If not provided and multiple subscriptions exist, you will be prompted. | (none) |
