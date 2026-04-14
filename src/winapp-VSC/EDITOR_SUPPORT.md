# AppxManifest Editor — Supported Customizations

This document details exactly which appxmanifest properties and elements the WinApp VS Code visual editor supports for viewing and editing, and which are not yet supported.

> **Tip:** If the editor doesn't support a customization your app requires, [file feedback](https://github.com/microsoft/winappCli/issues) to request additional feature support. You can always switch to the raw XML editor at any time using VS Code's **Open With…** command.

---

## Identity

| Property | Supported | Notes |
|----------|-----------|-------|
| `Name` | ✅ | Validated: letters, numbers, dots, hyphens; 3–50 chars; no reserved names (CON, PRN, etc.) |
| `Publisher` | ✅ | Validated: full X.500 distinguished name |
| `Version` | ✅ | Validated: `Major.Minor.Build.Revision` format, each part 0–65535 |
| `ProcessorArchitecture` | ✅ | Dropdown: x86, x64, arm, arm64, x86a64, neutral |
| `ResourceId` | ❌ | Preserved in XML but not shown in editor |

## Properties

| Property | Supported | Notes |
|----------|-----------|-------|
| `DisplayName` | ✅ | Validated: required, max 256 characters |
| `PublisherDisplayName` | ✅ | Validated: required, max 256 characters |
| `Description` | ✅ | Validated: max 2048 characters, no tabs/CR/LF |
| `Logo` (Store Logo) | ✅ | Validated: must be `.png`, `.jpg`, or `.jpeg` if extension is specified |
| `SupportedUsers` | ❌ | Preserved in XML but not shown in editor |

## Dependencies

### Target Device Families

| Property | Supported | Notes |
|----------|-----------|-------|
| `Name` | ✅ | |
| `MinVersion` | ✅ | Validated: `10.0.XXXXX.0` format |
| `MaxVersionTested` | ✅ | Validated: must be ≥ MinVersion |

Multiple target device families are supported. Families can be added and removed.

### Package Dependencies

| Property | Supported | Notes |
|----------|-----------|-------|
| `Name` | ✅ | Validated: required |
| `MinVersion` | ✅ | Validated: required |
| `Publisher` | ✅ | Validated: required |

Multiple package dependencies are supported. Dependencies can be added and removed.

## Resources

| Property | Supported | Notes |
|----------|-----------|-------|
| `Language` | ✅ | Validated: must be a valid BCP-47 tag (e.g. `en-us`, `fr-fr`, `zh-Hans-CN`) |
| `uap:Scale` | ✅ | Dropdown: 80, 100, 120, 125, 140, 150, 160, 175, 180, 200, 225, 250, 300, 350, 400, 450 |
| `uap:DXFeatureLevel` | ✅ | Dropdown: dx9, dx10, dx11, dx12 |

Multiple resource declarations are supported. Resources can be added and removed.

## Capabilities

The editor provides a checklist UI for commonly used capabilities organized by category.

### General Capabilities

| Capability | Supported |
|------------|-----------|
| `internetClient` | ✅ |
| `internetClientServer` | ✅ |
| `privateNetworkClientServer` | ✅ |
| `codeGeneration` | ✅ |

### Restricted Capabilities

| Capability | Supported |
|------------|-----------|
| `runFullTrust` | ✅ |
| `allowElevation` | ✅ |
| `unvirtualizedResources` | ✅ |
| `packagedShellExtension` | ✅ |

### Device Capabilities

| Capability | Supported |
|------------|-----------|
| `microphone` | ✅ |
| `webcam` | ✅ |
| `location` | ✅ |
| `bluetooth` | ✅ |

### Not Yet Supported

The following capability types are **not** available in the checklist but are preserved in the XML if already present:

- Additional general capabilities (e.g. `allJoyn`, `removableStorage`)
- Additional restricted capabilities (e.g. `broadFileSystemAccess`, `appCaptureSettings`)
- Additional device capabilities (e.g. `pointOfService`, `serialcommunication`, `usb`)
- Custom capabilities

## Applications

| Property | Supported | Notes |
|----------|-----------|-------|
| `Id` | ✅ | Validated: required, 1–64 chars, alpha-numeric fields separated by periods, each starting with a letter, no reserved names |
| `Executable` | ✅ | Validated: required, must end in `.exe` |
| `EntryPoint` | ✅ | Validated: required |

Multiple applications are supported.

### Visual Elements

| Property | Supported | Required | Notes |
|----------|-----------|----------|-------|
| `DisplayName` | ✅ | Yes | Validated: required, max 256 characters |
| `Description` | ✅ | No | Validated: max 2048 characters, no tabs/CR/LF |
| `BackgroundColor` | ✅ | Yes | Validated: hex color, `transparent`, or named color (e.g. cornflowerBlue) |
| `Square150x150Logo` | ✅ | Yes | Validated: must be `.png`, `.jpg`, or `.jpeg` |
| `Square44x44Logo` | ✅ | Yes | Validated: must be `.png`, `.jpg`, or `.jpeg` |
| `Wide310x150Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `Square71x71Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `Square310x310Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `BadgeLogo` (LockScreen) | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `SplashScreen Image` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `AppListEntry` | ❌ | No | Preserved in XML but not shown in editor |
| `ShowNameOnTiles` | ❌ | No | Preserved in XML but not shown in editor |

All visual asset paths are validated to ensure they use a supported format (`.png`, `.jpg`, or `.jpeg`). Empty paths are flagged as errors.

The **Regenerate Assets** button invokes the CLI to auto-generate all required icon sizes from a single source image.

### Application-Level Extensions

Extensions can be added via the **+ Add Extension** dropdown and edited as raw XML. The following extension templates are available:

| Extension | Category | Namespace |
|-----------|----------|-----------|
| MCP Server | `windows.appExtension` | `uap3` |
| COM Server | `windows.comServer` | `com` |
| App Execution Alias | `windows.appExecutionAlias` | `uap5` |
| Background Tasks | `windows.backgroundTasks` | *(default)* |
| Protocol Activation | `windows.protocol` | `uap` |
| File Type Association | `windows.fileTypeAssociation` | `uap` |
| Startup Task | `windows.startupTask` | `desktop` |
| Share Target | `windows.shareTarget` | `uap` |
| App Service | `windows.appService` | `uap` |
| Toast Notification Activation | `windows.toastNotificationActivation` | `desktop` |

Required XML namespace declarations are automatically added to the `<Package>` element when an extension template is inserted.

Extensions not matching these templates can still be added and will be preserved — they are displayed as editable raw XML blocks.

### Not Yet Supported: Application-Level Extensions

The following extension categories do **not** have templates but can be added manually via raw XML editing:

- `windows.appUriHandler`
- `windows.comInterface`
- `windows.outOfProcessServer`
- `windows.preInstalledConfigTask`
- And others — see [Microsoft documentation](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-application) for the full list

## Package-Level Extensions

**Not yet supported.** Package-level `<Extensions>` elements (direct children of `<Package>`, outside `<Applications>`) are **preserved** in the XML when editing but are not displayed or editable in the visual editor.

Common package-level extensions include:

- `windows.activatableClass.inProcessServer` — used for in-process COM/WinRT server hosting
- `windows.activatableClass.outOfProcessServer`

## Other Elements

The following manifest elements are **preserved** in the XML but are **not** displayed or editable in the visual editor:

| Element | Notes |
|---------|-------|
| `mp:PhoneIdentity` | Legacy phone identity; preserved if present |
| `build:Metadata` | Build metadata; preserved if present |
| `Extensions` (package-level) | See [Package-Level Extensions](#package-level-extensions) above |
| XML comments | Preserved in the raw XML |

## Validation Summary

The editor performs real-time inline validation for:

- **Required fields** — Identity name/publisher/version, display names, executable, entry point, logo paths
- **Format validation** — Version format, publisher DN format, hex/named colors, Windows version numbers, Application Id format
- **Length limits** — Display name (256), publisher display name (256), description (2048), Application Id (64)
- **Image format** — All visual asset paths must be `.png`, `.jpg`, or `.jpeg` files
- **Reserved names** — Identity Name and Application Id fields cannot use reserved device names (CON, PRN, etc.)
- **Character restrictions** — Description fields cannot contain tabs, carriage returns, or line feeds
- **Language tags** — Resource languages must be valid BCP-47 format (e.g. `en-us`)
- **Version ordering** — MaxVersionTested must be ≥ MinVersion

## XML Formatting

The editor uses surgical string replacement to modify the XML, preserving your original whitespace, indentation, and formatting style. The editor will not reformat or re-serialize your manifest.
