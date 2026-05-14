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
| `ResourceId` | ✅ | Optional field; addable via **+ ResourceId** button, removable via ✕ button |

## Properties

| Property | Supported | Notes |
|----------|-----------|-------|
| `DisplayName` | ✅ | Validated: required, max 256 characters. Supports literal values and MRT resource references (`ms-resource:`) |
| `PublisherDisplayName` | ✅ | Validated: required, max 256 characters |
| `Description` | ✅ | Validated: max 2048 characters, no tabs/CR/LF |
| `Logo` (Store Logo) | ✅ | Validated: must be `.png`, `.jpg`, `.jpeg`, or an MRT resource key (`ms-resource:`) |
| `SupportedUsers` | ❌ | Preserved in XML but not shown in editor |

## Phone Identity

| Property | Supported | Notes |
|----------|-----------|-------|
| `PhoneProductId` | ✅ | Optional section; addable via **+ PhoneIdentity** button, removable via ✕ button |
| `PhonePublisherId` | ✅ | Shown when PhoneIdentity section is present |

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

The editor provides a checklist UI for capabilities organized by category. Each capability includes a description tooltip.

### General Capabilities

| Capability | Supported |
|------------|-----------|
| `internetClient` | ✅ |
| `internetClientServer` | ✅ |
| `privateNetworkClientServer` | ✅ |
| `codeGeneration` | ✅ |
| `musicLibrary` | ✅ |
| `picturesLibrary` | ✅ |
| `videosLibrary` | ✅ |
| `removableStorage` | ✅ |
| `appointments` | ✅ |
| `contacts` | ✅ |
| `enterpriseAuthentication` | ✅ |
| `sharedUserCertificates` | ✅ |
| `phoneCall` | ✅ |
| `userAccountInformation` | ✅ |
| `voipCall` | ✅ |
| `objects3D` | ✅ |
| `chat` | ✅ |
| `blockedChatMessages` | ✅ |
| `backgroundMediaPlayback` | ✅ |
| `remoteSystem` | ✅ |
| `spatialPerception` | ✅ |
| `globalMediaControl` | ✅ |
| `graphicsCapture` | ✅ |
| `userDataTasks` | ✅ |
| `userNotificationListener` | ✅ |

### Restricted Capabilities

| Capability | Supported |
|------------|-----------|
| `runFullTrust` | ✅ |
| `allowElevation` | ✅ |
| `unvirtualizedResources` | ✅ |
| `packagedShellExtension` | ✅ |
| `appDiagnostics` | ✅ |
| `broadFileSystemAccess` | ✅ |
| `packageManagement` | ✅ |
| `packageQuery` | ✅ |
| `localSystemServices` | ✅ |
| `inputForegroundObservation` | ✅ |
| `confirmAppClose` | ✅ |

### Device Capabilities

| Capability | Supported |
|------------|-----------|
| `microphone` | ✅ |
| `webcam` | ✅ |
| `location` | ✅ |
| `bluetooth` | ✅ |
| `proximity` | ✅ |
| `usb` | ✅ |
| `humaninterfacedevice` | ✅ |
| `pointOfService` | ✅ |
| `wiFiControl` | ✅ |
| `radios` | ✅ |
| `optical` | ✅ |
| `activity` | ✅ |
| `serialcommunication` | ✅ |
| `gazeInput` | ✅ |
| `lowLevelDevices` | ✅ |
| `lowLevel` | ✅ |

### Not Yet Supported

The following capability types are **not** available in the checklist but are preserved in the XML if already present:

- Custom capabilities (e.g. `uap4:CustomCapability`)
- Capabilities using non-standard namespace prefixes

## Applications

| Property | Supported | Notes |
|----------|-----------|-------|
| `Id` | ✅ | Validated: required, 1–64 chars, alpha-numeric fields separated by periods, each starting with a letter, no reserved names |
| `Executable` | ✅ | Validated: required, must end in `.exe`. Browse button for file selection |
| `EntryPoint` | ✅ | Validated: required |
| `TrustLevel` | ✅ | Advanced attribute; shown under collapsible "Advanced Attributes" section |
| `RuntimeBehavior` | ✅ | Advanced attribute |
| `SupportsMultipleInstances` | ✅ | Advanced attribute |
| `Parameters` | ✅ | Advanced attribute |

Multiple applications are supported.

### Visual Elements

| Property | Supported | Required | Notes |
|----------|-----------|----------|-------|
| `DisplayName` | ✅ | Yes | Validated: required, max 256 characters |
| `Description` | ✅ | No | Validated: max 2048 characters, no tabs/CR/LF |
| `BackgroundColor` | ✅ | Yes | Validated: hex color, `transparent`, or named color (e.g. cornflowerBlue). Color picker provided |
| `Square150x150Logo` | ✅ | Yes | Validated: image format or MRT key. Browse button and live preview |
| `Square44x44Logo` | ✅ | Yes | Validated: image format or MRT key. Browse button and live preview |
| `Wide310x150Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `Square71x71Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `Square310x310Logo` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `BadgeLogo` (LockScreen) | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `SplashScreen Image` | ✅ | No | Shown if present; addable via **+ Add Visual Asset** |
| `AppListEntry` | ✅ | No | Optional field; addable via **+ Add Visual Asset** |
| `ShowNameOnTiles` | ✅ | No | Multi-select dropdown for tile sizes: `square150x150Logo`, `wide310x150Logo`, `square310x310Logo` |

All visual asset paths accept either a package-relative file path (`.png`, `.jpg`, `.jpeg`) or an MRT resource key (`ms-resource:` prefix). Unrecognized file extensions produce a warning (not an error) to allow MRT qualifier patterns. Empty paths for required assets are flagged as errors.

The **Regenerate Assets** button invokes the CLI to auto-generate all required icon sizes from a single source image.

### Application-Level Extensions

Extensions can be added via the **+ Add Extension** dropdown. Each extension is presented as an editable form with field-level descriptions and validation. The following extension templates are available:

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

#### Extension Field Validation

Each extension type has field-level validation:

| Field | Validation |
|-------|-----------|
| `Class.Id`, `ToastActivatorCLSID` | Must be a valid GUID (`{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`) |
| `ExecutionAlias.Alias` | Must end in `.exe`, no path separators or special characters |
| `Protocol.Name` | Lowercase letters, digits, `.`, `+`, `-` only; must start with a letter |
| `FileType` | Must start with `.` followed by alphanumeric characters |
| `FileTypeAssociation.Name` | Letters, digits, and periods only |
| `StartupTask.Enabled` | Must be `true` or `false` |
| `ExeServer.Executable` | Warning if not `.exe` or `.dll` |
| `Task.Type` | Warning for non-standard types (common: timer, pushNotification, systemEvent, general) |
| `AppService.Name` | Warning for non-reverse-domain format |

All required extension fields show an error when empty.

Extensions not matching these templates can still be added and will be preserved — they are displayed as editable form fields extracted from the XML structure.

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
| `build:Metadata` | Build metadata; preserved if present |
| `Extensions` (package-level) | See [Package-Level Extensions](#package-level-extensions) above |
| XML comments | Preserved in the raw XML |

## Validation Summary

The editor performs real-time inline validation for:

- **Required fields** — Identity name/publisher/version, display names, executable, entry point, logo paths, and extension-specific required fields
- **Format validation** — Version format, publisher DN format, hex/named colors, Windows version numbers, Application Id format, GUID format for COM CLSIDs, protocol name format, file extension format
- **Length limits** — Display name (256), publisher display name (256), description (2048), Application Id (64)
- **Image paths** — Visual asset paths must be `.png`, `.jpg`, `.jpeg` files or MRT resource keys (`ms-resource:`). Unrecognized extensions produce a warning to allow MRT qualifier patterns
- **Reserved names** — Identity Name and Application Id fields cannot use reserved device names (CON, PRN, etc.)
- **Character restrictions** — Description fields cannot contain tabs, carriage returns, or line feeds
- **Language tags** — Resource languages must be valid BCP-47 format (e.g. `en-us`)
- **Version ordering** — MaxVersionTested must be ≥ MinVersion
- **Extension fields** — GUID format, `.exe` suffix for aliases, protocol naming rules, file extension format, boolean values

## Save Behavior

The editor uses a debounce system for text input (300ms delay). When saving with Ctrl+S, any pending unsaved field changes are automatically flushed to the document before the save completes, preventing data loss from typing immediately before saving.

## XML Formatting

The editor uses surgical string replacement to modify the XML, preserving your original whitespace, indentation, and formatting style. The editor will not reformat or re-serialize your manifest.
