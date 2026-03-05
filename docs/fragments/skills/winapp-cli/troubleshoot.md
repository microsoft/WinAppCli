## When to use

Use this skill when:
- **Diagnosing errors** from winapp CLI commands
- **Choosing the right command** for a task
- **Understanding prerequisites** — what each command needs and what it produces

## Common errors & solutions

| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore` or `update` without config | Run `winapp init` first, or `cd` to the directory containing `winapp.yaml` |
| "appxmanifest.xml not found" | Running `package`, `create-debug-identity`, or `cert generate --manifest` | Run `winapp init` or `winapp manifest generate` first, or pass `--manifest <path>` |
| "Publisher mismatch" | Certificate publisher ≠ manifest publisher | Regenerate cert: `winapp cert generate --manifest`, or edit `appxmanifest.xml` `Identity.Publisher` to match |
| "Access denied" / "elevation required" | `cert install` without admin | Run terminal as Administrator for `winapp cert install` |
| "Package installation failed" | Cert not trusted, or stale package registration | `winapp cert install ./devcert.pfx` (admin), then `Get-AppxPackage <name> \| Remove-AppxPackage` |
| "Certificate not trusted" | Dev cert not installed on machine | `winapp cert install ./devcert.pfx` (admin) |
| "Build tools not found" | First run, tools not yet downloaded | Run `winapp update` to download tools; ensure internet access |
| "Failed to add package identity" | Stale debug identity or untrusted cert | `Get-AppxPackage *yourapp* \| Remove-AppxPackage` to clean up, then `winapp cert install` and retry |
| "Certificate file already exists" | `devcert.pfx` already present | Use `winapp cert generate --if-exists overwrite` or `--if-exists skip` |
| "Manifest already exists" | `appxmanifest.xml` already present | Use `winapp manifest generate --if-exists overwrite` or edit manifest directly |

## Command selection guide

```
Does the project have an appxmanifest.xml?
├─ No → Do you want full setup (manifest + config + optional SDKs)?
│       ├─ Yes → winapp init (adds Windows platform files to existing project)
│       └─ No, just a manifest → winapp manifest generate
└─ Yes
   ├─ Has winapp.yaml, cloned/pulled but .winapp/ folder missing?
   │  └─ winapp restore
   ├─ Want newer SDK versions?
   │  └─ winapp update
   ├─ Need a dev certificate?
   │  └─ winapp cert generate (then winapp cert install for trust)
   ├─ Need package identity for debugging?
   │  └─ winapp create-debug-identity <exe>
   ├─ Ready to create MSIX installer?
   │  └─ winapp package <build-output> --cert ./devcert.pfx
   ├─ Need to sign an existing file?
   │  └─ winapp sign <file> <cert>
   ├─ Need to update app icons?
   │  └─ winapp manifest update-assets ./logo.png
   ├─ Need to run SDK tools directly?
   │  └─ winapp tool <toolname> <args>
   ├─ Need to publish to Microsoft Store?
   │  └─ winapp store <args> (passthrough to Store Developer CLI)
   └─ Need the .winapp directory path for build scripts?
      └─ winapp get-winapp-path (or --global for shared cache)
```

**Important notes:**
- `winapp init` adds files to an **existing** project — it does not create a new project
- The key prerequisite for most commands is `appxmanifest.xml`, not `winapp.yaml`
- `winapp.yaml` is only needed for SDK version management (`restore`/`update`)
- Projects with NuGet package references (e.g., `.csproj` referencing `Microsoft.Windows.SDK.BuildTools`) can use winapp commands without `winapp.yaml`
- For Electron projects, use the npm package (`npm install --save-dev @microsoft/winappcli`) which includes Node.js-specific commands under `npx winapp node`

## Prerequisites & state matrix

| Command | Requires | Creates/Modifies |
|---------|----------|------------------|
| `init` | Existing project (any framework) | `winapp.yaml`, `.winapp/`, `appxmanifest.xml`, `Assets/`, `.gitignore` update |
| `restore` | `winapp.yaml` | `.winapp/packages/`, generated projections |
| `update` | `winapp.yaml` | Updates versions in `winapp.yaml`, reinstalls packages |
| `manifest generate` | Nothing | `appxmanifest.xml`, `Assets/` |
| `manifest update-assets` | `appxmanifest.xml` + source image | Regenerates `Assets/` icons |
| `cert generate` | Nothing (or `appxmanifest.xml` for publisher) | `devcert.pfx` |
| `cert install` | Certificate file + admin | Machine certificate store |
| `create-debug-identity` | `appxmanifest.xml` + exe + trusted cert | Registers sparse package with Windows |
| `package` | Build output + `appxmanifest.xml` | `.msix` file |
| `sign` | File + certificate | Signed file (in-place) |
| `create-external-catalog` | Directory with executables | `CodeIntegrityExternal.cat` |
| `tool <name>` | Nothing (auto-downloads tools) | Runs SDK tool directly |
| `store` | Nothing (auto-downloads Store CLI) | Passthrough to Microsoft Store Developer CLI |
| `get-winapp-path` | Nothing | Prints `.winapp` directory path |

## Debugging tips

- Add `--verbose` (or `-v`) to any command for detailed output
- Add `--quiet` (or `-q`) to suppress progress messages (useful in CI/CD)
- Run `winapp --cli-schema` to get the full JSON schema of all commands and options
- Run any command with `--help` for its specific usage information
- Use `winapp get-winapp-path` to find where packages are stored locally
- Use `winapp get-winapp-path --global` to find the shared cache location

## Getting more help

- Full CLI documentation: https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md
- Framework-specific guides: https://github.com/microsoft/WinAppCli/tree/main/docs/guides
- File an issue: https://github.com/microsoft/WinAppCli/issues
