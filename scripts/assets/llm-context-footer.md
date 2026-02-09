## Common Workflows

### New Project Setup
1. `winapp init .` - Initialize workspace with appxmanifest.xml, image assets, test certificate, and optionally SDK projections in the .winapp folder. (run with `--use-defaults` to make it non-interactive)
2. Edit `appxmanifest.xml` if you need to modify properties, set capabilities, or other configurations
3. Build your app
4. `winapp create-debug-identity <exe-path>` - to generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
5. Run the app
6. `winapp pack <output-folder-to-package> --cert .\devcert.pfx` - Create signed MSIX (--cert is optional)

### Existing Project (Clone/CI)
1. `winapp restore` - Reinstall packages and generate C++ projections from `winapp.yaml`
2. Build and package as normal

### Update SDK Versions
1. `winapp update` - Check for and install newer SDK versions
2. Rebuild your app

### Install SDKs After Initial Setup
If you ran `init` with `--setup-sdks none` (or skipped SDK installation) and later need the SDKs:
1. `winapp init --use-defaults --setup-sdks stable` - Re-run init to install SDKs
   - `--use-defaults` skips prompts and preserves existing files (manifest, cert, etc.)
   - Use `--setup-sdks preview` or `--setup-sdks experimental` for preview/experimental SDK versions
2. Rebuild your app with the new SDK projections in `.winapp/`

### Debug with Package Identity
For apps that need Windows APIs requiring identity (push notifications, etc.):
1. Ensure an appxmanifest.xml is present, either via `winapp init` or `winapp manifest generate`
2. `winapp create-debug-identity ./myapp.exe` - generate package identity from generated appxmanifest.xml before running the app so the exe has package identity
3. Run your app - it now has package identity

### Electron Apps
1. `winapp init` - Set up workspace (run with --use-defaults to make it non-interactive)
2. `winapp node create-addon --template cs` - Generate native C# addon for Windows APIs (`--template cpp` for C++ addon)
3. `winapp node add-electron-debug-identity` - Enable identity for debugging
4. `npm start` to launch app normally, but now with identity
5. For production, create production files with the preferred packager and run `winapp pack <generated-production-files> --cert .\devcert.pfx`

## Command Selection Guide

Use this decision tree to pick the right command:

```
Using winapp CLI in a new project?
├─ Yes → run winapp init
│        (creates manifest + cert + SDKs + config)
└─ No
   ├─ Cloned/pulled a repo with winapp.yaml?
   │  └─ Yes → winapp restore
   │           (reinstalls SDKs from config)
   ├─ Want newer SDK versions?
   │  └─ Yes → winapp update
   │           (checks NuGet, updates config)
   ├─ Only need a manifest (no SDKs/cert)?
   │  └─ Yes → winapp manifest generate
   ├─ Only need a dev certificate?
   │  └─ Yes → winapp cert generate
   ├─ Ready to create MSIX installer?
   │  └─ Yes → winapp package <build-output>
   │           (add --cert for signing)
   ├─ Need to add package identity for debugging Windows APIs that need it?
   │  └─ Yes → winapp create-debug-identity <exe>
   │           (enables push notifications, etc.)
   └─ Need to run SDK tools directly?
      └─ Yes → winapp tool <toolname> <args>
               (makeappx, signtool, makepri)
```

## Prerequisites & State

| Command | Requires | Creates/Modifies |
|---------|----------|------------------|
| `init` | Nothing | `winapp.yaml`, `.winapp/`, `appxmanifest.xml`, `Assets/`, `devcert.pfx` |
| `restore` | `winapp.yaml` | `.winapp/packages/` |
| `update` | `winapp.yaml` | Updates versions in `winapp.yaml` |
| `manifest generate` | Nothing | `appxmanifest.xml`, `Assets/` |
| `cert generate` | Nothing (or `appxmanifest.xml` for publisher inference) | `*.pfx` file |
| `package` | App build output + `appxmanifest.xml` (+ `devcert.pfx` for optional signing) | `*.msix` file |
| `create-debug-identity` | `appxmanifest.xml` + exe | Registers sparse package with Windows |

## Common Errors & Solutions

| Error | Cause | Solution |
|-------|-------|----------|
| "winapp.yaml not found" | Running `restore` or `update` without config | Run `winapp init` first, or ensure you're in the right directory |
| "appxmanifest.xml not found" | Running `package` or `create-debug-identity` without manifest | Run `winapp init` or `winapp manifest generate` first |
| "Publisher mismatch" | Certificate publisher doesn't match manifest | Regenerate cert with `--manifest` flag, or edit manifest Publisher to match |
| "Access denied" / "elevation required" | `cert install` without admin | Run terminal as Administrator |
| "Package installation failed" | Signing issue or existing package conflict | Run `Get-AppxPackage <name> | Remove-AppxPackage` first, ensure cert is trusted |
| "Certificate not trusted" | Dev cert not installed on machine | Run `winapp cert install ./devcert.pfx` as admin |
| "Build tools not found" | First run, tools not downloaded yet | winapp auto-downloads; ensure internet access. Or run `winapp tool --help` to trigger download |

## Machine-Readable Schema

For programmatic access to the complete CLI structure including all options, types, and defaults:

```bash
winapp --cli-schema
```

This outputs JSON that can be parsed by tools and LLMs. See [cli-schema.json](cli-schema.json).
