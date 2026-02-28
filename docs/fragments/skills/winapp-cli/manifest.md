## When to use

Use this skill when:
- **Creating `appxmanifest.xml`** for a project that doesn't have one yet
- **Generating app icon assets** from a single source image
- **Understanding manifest structure** for package identity and capabilities

## Prerequisites

- winapp CLI installed
- Optional: a source image (PNG, at least 400x400 pixels) for custom app icons

## Key concepts

**`appxmanifest.xml`** is the key prerequisite for most winapp commands — it's more important than `winapp.yaml`. It declares:
- **Package identity** — name, publisher, version
- **App entry point** — which executable to launch
- **Capabilities** — what the app can access (internet, file system, etc.)
- **Visual assets** — icons for Start menu, taskbar, installers
- **Extensions** — share target, startup tasks, file associations, etc.

**Two manifest templates:**
- **`packaged`** (default) — for full MSIX distribution
- **`sparse`** — for desktop apps that need package identity without full MSIX containment (uses `AllowExternalContent`)

**`winapp init` also generates a manifest** as part of full project setup. Use `winapp manifest generate` when you only need the manifest without SDK setup or `winapp.yaml`.

## Usage

### Generate a new manifest

```powershell
# Defaults — uses current folder name, current user as publisher
winapp manifest generate

# Into a specific directory
winapp manifest generate ./my-project

# Customize identity
winapp manifest generate --package-name "MyApp" --publisher-name "CN=Contoso" --version "2.0.0.0"

# Set entry point and description
winapp manifest generate --executable myapp.exe --description "My awesome app"

# Generate a sparse manifest (for desktop apps needing identity without full MSIX)
winapp manifest generate --template sparse

# Overwrite existing manifest
winapp manifest generate --if-exists overwrite
```

Output:
- `appxmanifest.xml` — the manifest file
- `Assets/` — default app icons in required sizes (Square44x44Logo, Square150x150Logo, Wide310x150Logo, etc.)

### Update app icons from a source image

```powershell
# Generate all required icon sizes from one source image
winapp manifest update-assets ./my-logo.png

# Specify manifest location (if not in current directory)
winapp manifest update-assets ./my-logo.png --manifest ./path/to/appxmanifest.xml
```

The source image should be at least 400x400 pixels (PNG recommended). The command reads the manifest to determine which asset sizes are needed and generates them all.

## Manifest structure overview

A typical `appxmanifest.xml` looks like:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="MyApp" Publisher="CN=MyPublisher" Version="1.0.0.0" />
  <Properties>
    <DisplayName>My App</DisplayName>
    <PublisherDisplayName>My Publisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="My App" Description="My Application"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" BackgroundColor="transparent" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

Key fields to edit:
- `Identity.Name` — unique package name (no spaces)
- `Identity.Publisher` — must match your certificate exactly
- `Application.Executable` — your app's exe filename
- `Capabilities` — add capabilities as needed (`internetClient`, `broadFileSystemAccess`, etc.)

## Tips

- Always ensure `Identity.Publisher` matches your signing certificate — use `winapp cert generate --manifest` to auto-match
- The `sparse` template adds `uap10:AllowExternalContent="true"` for apps that need identity but run outside the MSIX container
- You can manually edit `appxmanifest.xml` after generation — it's a standard XML file
- Image assets must match the paths referenced in the manifest — `update-assets` handles this automatically
- For logos, transparent PNGs work best. Use a square image for best results across all sizes.

## Troubleshooting
| Error | Cause | Solution |
|-------|-------|----------|
| "Manifest already exists" | `appxmanifest.xml` present | Use `--if-exists overwrite` to replace, or edit existing file directly |
| "Invalid source image" | Image too small or wrong format | Use PNG, at least 400x400 pixels |
| "Publisher mismatch" during packaging | Manifest publisher ≠ cert publisher | Edit `Identity.Publisher` in manifest, or regenerate cert with `--manifest` |
