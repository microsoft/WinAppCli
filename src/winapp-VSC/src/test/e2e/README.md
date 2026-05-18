# Manifest Editor – End-to-End Tests

Automated Playwright tests that launch VS Code as an Electron app, open an AppxManifest file in the custom WinApp editor, and interact with every tab and control in the webview UI.

## Quick start

```powershell
cd src/winapp-VSC

# Install dependencies (first time only)
npm install

# Run the full E2E suite
npm run test:e2e
```

> **Prerequisites** – VS Code must be installed at the default location and all other VS Code windows must be closed before running (Playwright needs exclusive access to the Electron process).

## Architecture

| File | Purpose |
|------|---------|
| `playwright.config.ts` | Playwright configuration – 60 s timeout, 1 retry, 1 worker, trace-on-failure |
| `helpers.ts` | Shared launch / teardown / interaction utilities (tab switching, field editing, validation checks, XML round-trip reads) |
| `shared-context.ts` | Singleton manager – launches one VS Code instance and reuses it across all specs via `ensureEditor()` and `resetManifest()` |
| `global-teardown.ts` | Calls `closeSharedEditor()` after the entire suite completes |

### Fixture manifests

Tests run against real AppxManifest files stored in `src/test/fixtures/`:

| Fixture | Used by |
|---------|---------|
| `winui-gallery.appxmanifest` | Default – used by the 7 core tab specs and the parse-error spec |
| `push-notifications-sample.appxmanifest` | `push-notifications-fixture.spec.ts` |
| `background-task-sample.appxmanifest` | `background-task-fixture.spec.ts` |
| `widgets-sample.appxmanifest` | Unit tests for nested Capabilities and package-level Extensions |
| `edge-cases.appxmanifest` | Unit tests for edge-case / adversarial parsing |

`resetManifest(ctx, fixtureName?)` copies the selected fixture into the workspace before each spec file, waits for the editor to reload, and re-acquires the webview frame.

---

## Test inventory (125 tests)

### `editor-launch.spec.ts` — 11 tests

Validates that the custom editor launches correctly, all tabs render, and global UI elements are present.

| # | Test | Validates |
|---|------|-----------|
| 1 | manifest editor opens and shows tab bar | Custom editor webview loads and the tab bar is visible |
| 2 | Identity tab is active by default | Identity is selected and its panel is shown on open |
| 3 | all six tabs are visible | Identity, Properties, Dependencies, Resources, Applications, and Capabilities tabs exist |
| 4 | can switch to Properties tab | Tab switching to Properties works and hides Identity |
| 5 | can switch to Dependencies tab | Tab switching to Dependencies works |
| 6 | can switch to Resources tab | Tab switching to Resources works |
| 7 | can switch to Applications tab | Tab switching to Applications works |
| 8 | can switch to Capabilities tab | Tab switching to Capabilities works |
| 9 | can switch back to Identity tab | Returning to Identity works and it becomes selected |
| 10 | View XML button is visible | View XML button exists with the expected label |
| 11 | info banner with feedback link is visible | Info banner and GitHub feedback link are present |

### `identity-tab.spec.ts` — 18 tests

Validates all Identity tab fields, validation rules, processor architecture, ResourceID, and PhoneIdentity.

| # | Test | Validates |
|---|------|-----------|
| 1 | name field is populated from manifest | Manifest Name loads into the Identity Name field |
| 2 | publisher field is populated from manifest | Publisher loads into the Identity Publisher field |
| 3 | version field is populated from manifest | Version loads into the Identity Version field |
| 4 | editing name field updates the XML document | Name edits persist to `Name="..."` in XML |
| 5 | editing version field updates the XML document | Version edits persist to XML |
| 6 | clearing name shows validation error | Empty name triggers validation/error styling |
| 7 | entering valid name clears validation error | Valid name removes the error state |
| 8 | invalid version format shows validation error | Bad version format is rejected |
| 9 | valid version clears error | Valid version removes the error state |
| 10 | processor architecture custom select displays current value | Processor architecture select renders a current value |
| 11 | can change processor architecture | Selecting x64 writes `ProcessorArchitecture="x64"` to XML |
| 12 | add Resource ID button is visible | Optional Resource ID add button is present |
| 13 | clicking Add Resource ID shows the field | Clicking Add Resource ID reveals the field group |
| 14 | entering a Resource ID updates the XML | ResourceId edits persist to XML |
| 15 | Phone Identity section is visible | PhoneIdentity section appears when present in the fixture |
| 16 | Phone Identity fields are populated | Phone Product ID and Publisher ID populate from the manifest |
| 17 | editing Phone Identity updates the XML | Phone Product ID edits persist to XML |
| 18 | remove Phone Identity button removes the section | Removing PhoneIdentity deletes it from XML |

### `properties-tab.spec.ts` — 10 tests

Validates Properties tab fields, validation, logo browse button, and package type selector.

| # | Test | Validates |
|---|------|-----------|
| 1 | display name field is populated | DisplayName loads from the manifest |
| 2 | publisher display name is populated | PublisherDisplayName loads from the manifest |
| 3 | logo path is populated | Logo path loads from the manifest |
| 4 | editing display name updates the XML | DisplayName edits persist to XML |
| 5 | editing publisher display name updates the XML | PublisherDisplayName edits persist to XML |
| 6 | editing logo path updates the XML | Logo edits persist to XML |
| 7 | clearing display name shows validation error | Empty DisplayName triggers validation |
| 8 | clearing publisher display name shows validation error | Empty PublisherDisplayName triggers validation |
| 9 | restoring values clears errors | Restoring both values clears error states |
| 10 | package type selector is visible | Package type dropdown is rendered |

### `dependencies-tab.spec.ts` — 16 tests

Validates target device families, package dependencies, and all dependency sub-types (add/edit/remove).

| # | Test | Validates |
|---|------|-----------|
| 1 | shows existing target device family from fixture | Target device family entries are loaded |
| 2 | target device family fields are populated | Target family title contains Windows.Desktop |
| 3 | add target device family dropdown is visible | Add-family control is present |
| 4 | can add a target device family via dropdown | Adding a family increases the list count |
| 5 | can edit target device family minVersion | MinVersion edits persist to XML |
| 6 | can remove a target device family | Removing a family decreases the list count |
| 7 | add package dependency button is visible | Package dependency add control exists |
| 8 | can add a package dependency | Adding a dependency increases the list count |
| 9 | can edit package dependency name | Package dependency name edits persist to XML |
| 10 | can remove a package dependency | Removing a dependency decreases the list count |
| 11 | add main package dependency button is visible | Main package dependency add control exists |
| 12 | add driver constraint button is visible | Driver constraint add control exists |
| 13 | add OS package dependency button is visible | OS package dependency add control exists |
| 14 | add host runtime dependency button is visible | Host runtime dependency add control exists |
| 15 | add external dependency button is visible | External dependency add control exists |
| 16 | can add and remove a main package dependency | Main package dependency round-trip works |

### `resources-tab.spec.ts` — 6 tests

Validates resource list rendering, add/edit/move/remove operations.

| # | Test | Validates |
|---|------|-----------|
| 1 | shows existing resources from fixture | Resources list is initially populated |
| 2 | resource language field is populated | First resource language loads as `x-generate` |
| 3 | add resource button is visible | Add resource button exists |
| 4 | can add a new resource | Clicking add increases the resource count |
| 5 | can edit resource language | Resource language edits persist to XML |
| 6 | can remove a resource | Removing a resource decreases the count |

### `applications-tab.spec.ts` — 18 tests

Validates application cards, sub-tabs (Info, Extensions, Visual Assets), extension CRUD, visual asset CRUD, and add/remove applications.

| # | Test | Validates |
|---|------|-----------|
| 1 | shows at least one application card | At least one app card renders |
| 2 | application card has title | App card title is visible |
| 3 | app Info sub-tab is visible by default | Info is the default sub-tab |
| 4 | app id field is populated | App ID loads as `App` |
| 5 | app executable field is populated | Executable field is populated |
| 6 | editing app id updates the XML | App ID edits persist to XML |
| 7 | editing app executable updates the XML | Executable edits persist to XML |
| 8 | can switch to Extensions sub-tab | Extensions sub-tab becomes visible |
| 9 | existing extensions are shown | Extension items render if present |
| 10 | can add a Protocol Activation extension and fill its fields | Adding extension via dropdown works, filling Name field persists to XML |
| 11 | can remove the newly added extension | Removing an extension decreases the count |
| 12 | can switch to Visual Assets sub-tab | Visual Assets sub-tab becomes visible |
| 13 | visual asset fields are present | Visual asset inputs are rendered |
| 14 | can add a Splash Screen visual asset via dropdown | Adding optional Splash Screen asset via dropdown works and persists to XML |
| 15 | add application button is visible | Add application button exists |
| 16 | can add a new application | Adding an app increases card count |
| 17 | can remove the newly added application | Removing an app decreases card count |
| 18 | browse executable button is visible | Executable browse button exists |

### `capabilities-tab.spec.ts` — 16 tests

Validates all four capability categories, checkbox toggling, hover descriptions, and custom capability CRUD.

| # | Test | Validates |
|---|------|-----------|
| 1 | General capabilities section is visible | General category renders |
| 2 | Restricted capabilities section is visible | Restricted category renders |
| 3 | Device capabilities section is visible | Device category renders |
| 4 | Custom Capability section is visible | Custom category renders |
| 5 | runFullTrust capability is checked (from fixture) | Restricted `runFullTrust` capability is preselected |
| 6 | can check internetClient capability | Toggling internetClient on updates UI and XML |
| 7 | can uncheck internetClient capability | Toggling internetClient off updates UI and XML |
| 8 | can toggle a device capability | Device capability can be checked and unchecked |
| 9 | description panel exists | Capability description panel is present |
| 10 | hovering a capability shows description | Hovering populates description text |
| 11 | custom capability input is visible | Custom capability input and add button are present |
| 12 | adding empty custom capability shows error | Empty submission shows a required error |
| 13 | adding invalid format custom capability shows error | Invalid format is rejected |
| 14 | typing in input clears validation error | Typing clears the custom capability error |
| 15 | adding valid custom capability succeeds | Valid custom capability is accepted and written to XML |
| 16 | custom capability appears in the custom capabilities list | New custom capability appears in the list |

### `parse-error.spec.ts` — 1 test

Validates error handling for malformed manifests. This spec launches its own VS Code instance with broken XML.

| # | Test | Validates |
|---|------|-----------|
| 1 | shows error view for malformed XML | Malformed manifest XML opens the error view with expected message and "Open in Text Editor" action |

### `push-notifications-fixture.spec.ts` — 8 tests

Validates the editor against the push-notifications-sample fixture, covering identity, properties, dependencies, applications, and capabilities.

| # | Test | Validates |
|---|------|-----------|
| 1 | identity fields are populated correctly | Identity fields load from the push notifications fixture |
| 2 | properties fields are populated correctly | Properties fields (DisplayName, PublisherDisplayName, Logo) load correctly |
| 3 | has one target device family (Windows.Universal) | Exactly one target device family exists and is Windows.Universal |
| 4 | application card is present with correct id | App card exists and its ID is `App` |
| 5 | application has extensions | Extension elements are present in the application |
| 6 | internetClient capability is checked | internetClient is enabled in the fixture |
| 7 | runFullTrust restricted capability is checked | Restricted `rescap:runFullTrust` is enabled in the fixture |
| 8 | editing identity name persists to XML | Editing the name writes back to XML |

### `background-task-fixture.spec.ts` — 17 tests

Validates the editor against the background-task-sample fixture, which exercises PhoneIdentity, multiple device families, all five dependency sub-types, empty resources, custom capabilities, and restricted capabilities.

| # | Test | Validates |
|---|------|-----------|
| 1 | identity fields are populated correctly | Identity fields load from the background task fixture |
| 2 | PhoneIdentity section is visible | PhoneIdentity section is present |
| 3 | PhoneIdentity fields are populated | PhoneIdentity values are loaded correctly |
| 4 | properties fields are populated correctly | Properties fields (DisplayName, PublisherDisplayName, Logo) load correctly |
| 5 | has two target device families | Exactly two target device families exist |
| 6 | first device family is Windows.Universal | First target family is Windows.Universal |
| 7 | second device family is Windows.Desktop | Second target family is Windows.Desktop |
| 8 | main package dependencies are present | Main package dependencies exist |
| 9 | driver constraints are present | Driver constraints exist |
| 10 | OS package dependencies are present | OS package dependencies exist |
| 11 | host runtime dependencies are present | Host runtime dependencies exist |
| 12 | external dependencies are present | External dependencies exist |
| 13 | resources section has no items (empty Resources element) | Resources list is empty |
| 14 | application card is present | App card exists with ID `App` |
| 15 | runFullTrust restricted capability is checked | Restricted `rescap:runFullTrust` is enabled |
| 16 | custom capability is listed | Custom capabilities are rendered |
| 17 | editing display name persists to XML | DisplayName edits write back to XML |

---

## Known flaky tests

These tests occasionally fail on the first attempt but pass on retry (Playwright's built-in retry handles them):

| Test | Reason |
|------|--------|
| `background-task-fixture.spec.ts` › identity fields are populated correctly | Timing — editor may not have fully reloaded after fixture swap |
| `parse-error.spec.ts` › shows error view for malformed XML | Launches its own VS Code instance — sensitive to startup timing |
