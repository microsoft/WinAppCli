# UI Automation

Inspect and interact with running Windows applications from the command line.
Used by AI agents and developers for UI testing, debugging, and automation.

## Overview

`winapp ui` provides commands for inspecting and interacting with Windows app UIs.
Uses Windows UI Automation (UIA). Works with any Windows app — WPF, WinForms, Win32, Electron, and WinUI 3.
Most commands drive the app through UIA patterns (no input injection); `ui click` is the exception and uses real mouse simulation for controls that don't support `InvokePattern`.

## Quick Start

```bash
# Connect to any app and see its UI tree
winapp ui inspect -a notepad

# Find specific elements
winapp ui search Button -a notepad

# Activate an element
winapp ui invoke Close -a notepad

# Take a screenshot
winapp ui screenshot -a notepad
```

## Targeting Apps

### By process name
```bash
winapp ui inspect -a notepad
winapp ui inspect -a slack            # auto-picks visible window for multi-process apps
winapp ui inspect -a imageresizer     # partial match: finds PowerToys.ImageResizer
```

### By window title
```bash
winapp ui inspect -a "LICENSE - Notepad"
winapp ui inspect -a "Fix WinApp"     # partial title match
```

### By PID
```bash
winapp ui inspect -a 12345
```

### By HWND (stable — survives tab/title changes)
```bash
# Discover HWNDs
winapp ui list-windows -a Terminal
  → HWND 985238: "🤖 Testing" (WindowsTerminal, PID 21228)
  → HWND 131906: "Fix WinApp" (WindowsTerminal, PID 21228)

# Target specific window
winapp ui inspect -w 131906
winapp ui screenshot -w 131906
```

Use `-a` for discovery, `-w` for stable targeting. When `-a` matches multiple windows, the command lists them with HWNDs for you to pick.

## Selectors

Target elements using the selector shown in `[brackets]` in inspect/search output.
There are three types of selectors:

| Selector | Meaning | Example |
|---|---|---|
| `MinimizeButton` | AutomationId (shown when unique — stable, preferred) | `winapp ui invoke MinimizeButton -a myapp` |
| `btn-close-d1a0` | Semantic slug (shown when no unique AutomationId) | `winapp ui invoke btn-close-d1a0 -a myapp` |
| `Submit` | Plain-text search against Name/AutomationId (case-insensitive substring) | `winapp ui invoke Submit -a myapp` |

**AutomationId selectors** are developer-set identifiers (`AutomationProperties.AutomationId` in XAML).
When an AutomationId is unique across the entire UI tree, `inspect` and `search` show it directly
as the selector — these survive layout changes, localization, and tree restructuring.

**Slug selectors** (e.g., `btn-close-d1a0`) are generated when no unique AutomationId exists.
Format: `prefix-name-hash`. The hash validates element identity but may go stale after UI changes.

### Inspect output format

The `inspect` command shows the element tree with colored output (selector in cyan, name in green, metadata in gray):
```
TabView Tab (0,-1 1200x48)
  TabListView List (4,-1 1100x48)
    tab-newtab-5f5b TabItem "New Tab" (14,-1 200x48)
  NewTabButton SplitButton "New Tab" [collapsed] (1104,5 96x36)
Found 10 elements (--depth 3). Use the first token as selector, e.g.: winapp ui invoke TabView -a terminal
```

The **first word** on each line is the selector — use it with other `ui` commands.
When an element has a unique AutomationId, it's used directly (e.g., `TabView`, `NewTabButton`).
When no unique AutomationId exists, a generated slug is used (e.g., `tab-newtab-5f5b`).

### Semantic slugs

Slugs use the format: `prefix-normalizedname-hash` where:
- **prefix** — 3-letter type abbreviation (btn, txt, chk, cmb, itm, tab, img, lbl, pn, win, grp, lnk, mnu, etc.)
- **normalizedname** — lowercase alphanumeric from AutomationId (preferred) or Name, max 15 chars
- **hash** — 4-char hex hash of the element's RuntimeId (validates element identity)

Slugs are shell-safe (no special characters), unique, and can be used directly as arguments. The hash provides staleness detection — if the element has been replaced, you get: "Element may have changed. Re-run inspect."

Elements with no name or AutomationId show only prefix + hash (e.g., `pn-c8a3`).

### Disambiguating multiple matches

Slugs from `inspect`/`search` output are unique, but can change across layout changes - use them over plain type names or text when multiple matches. When a selector is ambiguous, the CLI prints all matches with their slugs so you can pick the right one and re-run with that slug.

```bash
winapp ui search Button -a myapp            # shows: btn-ok-a1b2 "OK", btn-cancel-c3d4 "Cancel"
winapp ui invoke btn-ok-a1b2 -a myapp       # invoke using slug (preferred)
winapp ui invoke btn-cancel-c3d4 -a myapp   # invoke the other Button by its slug
```

### Plain text search
Use plain text to search for elements — no special syntax needed:
```bash
winapp ui search Minimize -a notepad        # finds elements with "Minimize" in Name or AutomationId
winapp ui search Close -a notepad           # case-insensitive substring match
winapp ui invoke Minimize -a notepad        # search + invoke in one step (disambiguates if needed)
winapp ui search "Save" -a notepad          # find elements containing "Save"
winapp ui search "error" -a myapp           # case-insensitive match
```

When a text search matches multiple elements (e.g., SettingsExpander where Group, Button, and Text all share the same name), the CLI automatically picks the only invokable element. If multiple are invokable, it lists all matches with slugs.

For non-invokable search results (e.g., a TextBlock inside a Button), the search
automatically surfaces the nearest **invokable ancestor** — the parent element you can use with `invoke`.
This works for all search selectors:

```
  lbl-savechanges-a1b2 "Save changes" (120,40 80x20)
        ^ invoke via: btn-save-c3d4 "Save"
```

The surfaced selector can be used directly:
```bash
winapp ui invoke btn-save-c3d4 -a myapp    # invoke the parent Button
```

## Commands

### status
Connect to an app and show connection info.
```bash
winapp ui status -a notepad
winapp ui status -a notepad --json
```

### inspect
View the UI element tree. Output shows semantic slugs with 2-space indentation for hierarchy:
```bash
winapp ui inspect -a notepad                    # full window tree, depth 3
winapp ui inspect -a notepad --depth 5          # deeper tree
winapp ui inspect txt-searchbox-e5f6 -a notepad # subtree rooted at element
winapp ui inspect --ancestors btn-close-d1a2 -a notepad  # walk up from element to root
winapp ui inspect -a myapp --interactive        # invokable elements only, auto-depth 8
winapp ui inspect -a myapp --hide-disabled      # hide disabled elements
winapp ui inspect -a myapp --hide-offscreen     # hide offscreen elements
```

Example output (default):
```
win-aidevgalleryp-f1a3 "AI Dev Gallery Preview" (94,206 1280x1023)
  pn-c8a3 (102,207 1264x1014)
    btn-minimize-d1a0 "Minimize" (1222,206 48x48)
    btn-maximize-e2b1 "Maximize" (1270,206 48x48)
    itm-samples-3f2c "Samples" (102,330 72x62)
```

Example output (`--interactive` — invokable elements only, flat list):
```
btn-minimize-d1a0 "Minimize" (1222,206 48x48)
btn-maximize-e2b1 "Maximize" (1270,206 48x48)
btn-close-d1a2 "Close" (1318,206 48x48)
itm-home-7b3e "Home" (102,268 72x62)
itm-samples-3f2c "Samples" (102,330 72x62)
itm-models-9a4f "Models" (102,392 72x62)
```

Elements may show these state markers:
- `[on]` / `[off]` / `[indeterminate]` — toggle/checkbox state
- `[collapsed]` / `[expanded]` — expand/collapse state for trees, combo boxes, menu items
- `[scroll:v]` / `[scroll:h]` / `[scroll:vh]` — scrollable container (vertical, horizontal, or both)
- `[offscreen]` — element is not visible on screen
- `[disabled]` — element is not enabled
- `value="..."` — current text content for editable elements (when different from Name)

### search
Find elements matching a selector. Output shows semantic slugs:
```bash
winapp ui search Button -a notepad              # all buttons
winapp ui search Close -a notepad               # finds elements with "Close" in name
winapp ui search SearchBox -a notepad           # finds elements with "SearchBox" in name or AutomationId
winapp ui search Button --max 10 -a notepad     # limit results
```

Example output:
```
  btn-minimize-d1a0 "Minimize" (1222,206 48x48)
  btn-maximize-e2b1 "Maximize" (1270,206 48x48)
  btn-close-d1a2 "Close" (1318,206 48x48)
```

Slugs shown in output (e.g., `btn-minimize-d1a0`) can be used directly with other commands:
```bash
winapp ui invoke btn-minimize-d1a0 -a notepad
```

### get-property
Read property values from an element. Includes pattern-specific state (ToggleState, Value, IsSelected, etc.).
```bash
winapp ui get-property btn-submit-7a90 -a myapp              # all properties
winapp ui get-property chk-checkbox-b2c3 -p ToggleState -a myapp   # checkbox state
winapp ui get-property txt-textbox-a4b1 -p Value -a myapp          # current text value
winapp ui get-property cmb-combobox-d5e6 -p ExpandCollapseState -a myapp  # expanded or collapsed
```

### screenshot
Capture a window or element as PNG. When multiple windows exist (e.g., app + open dialog), they are composited into a single PNG with each window stitched in.
```bash
winapp ui screenshot -a notepad                     # saves screenshot.png in cwd
winapp ui screenshot -a notepad --output my.png     # custom filename
winapp ui screenshot -a notepad --json              # returns file path as JSON
winapp ui screenshot -w 131906                      # target specific HWND (+ its dialogs)
winapp ui screenshot txt-searchbox-e5f6 -a myapp          # crop to element bounds
winapp ui screenshot -a myapp --capture-screen      # capture from screen (includes popups/overlays)
```

When dialogs or popups are open, all windows are composited into one PNG so you can see the full UI state in a single image.

Use `--capture-screen` when you need to capture popup menus, dropdowns, flyouts, or tooltip overlays. In `--capture-screen` mode (and when retrying after a blank-frame is detected) the target window is brought to the foreground first; normal window captures do not move the window.

### invoke
Programmatically activate an element (click button, toggle checkbox, expand combo box).
```bash
winapp ui invoke btn-submit-7a90 -a myapp             # by slug from inspect
winapp ui invoke btn-submit-a1b2 -a myapp  # by slug from inspect/search
winapp ui invoke cmb-sizecombobox-b4c5 -a myapp # expand combo box
```

Tries patterns in order: InvokePattern → TogglePattern → SelectionItemPattern → ExpandCollapsePattern.

### click
Click an element at its screen coordinates using mouse simulation. Use this for controls that don't support `InvokePattern` (e.g., column headers, list items).
```bash
winapp ui click btn-column1-a3f2 -a myapp              # single click by slug
winapp ui click "Column1" -a myapp                      # single click by text search
winapp ui click btn-column1-a3f2 -a myapp --double      # double-click
winapp ui click btn-column1-a3f2 -a myapp --right       # right-click
```

### set-value
Set a value on an editable element (text for TextBox/ComboBox, number for Slider).
```bash
winapp ui set-value txt-textbox-a4b1 "Hello world" -a notepad
winapp ui set-value sld-volume-b2c3 75 -a myapp
```

### get-value
Read the current value from an element. Uses a smart fallback chain: TextPattern (RichEditBox, Document) → ValuePattern (TextBox, Slider) → SelectionPattern (ComboBox, RadioButton, TabView) → Name (labels).
```bash
winapp ui get-value doc-texteditor-53ad -a notepad          # read full document text
winapp ui get-value SearchBox -a myapp                      # read TextBox content
winapp ui get-value CmbTheme -a myapp                       # read ComboBox selected item
winapp ui get-value sld-volume-b2c3 -a myapp                # read Slider value
winapp ui get-value lbl-title-a1b2 -a myapp --json          # JSON: { "elementId": "...", "text": "..." }
```

### focus
Move keyboard focus to an element.
```bash
winapp ui focus txt-textbox-a4b1 -a notepad
```

### scroll-into-view
Scroll an element into the visible area.
```bash
winapp ui scroll-into-view itm-targetitem-c3d4 -a myapp
```

### wait-for
Wait for an element to appear, disappear, or have a value reach a target.
```bash
winapp ui wait-for Button -a myapp --timeout 5000                       # wait for any button
winapp ui wait-for btn-submit-7a90 -a myapp --timeout 5000             # wait for specific element
winapp ui wait-for CounterDisplay -a myapp --value "5" --timeout 5000  # wait for element value (smart fallback)
winapp ui wait-for lbl-status -a myapp --property Name --value "Done" --timeout 5000  # wait for specific property
winapp ui wait-for btn-submit-a1b2 --gone -a myapp --timeout 2000      # wait for element to disappear
winapp ui wait-for lbl-status -a myapp --value "Done" --contains       # substring match instead of exact equality
```

### scroll
Scroll a container element. Find scrollable containers with `search scroll` — look for `[scroll:v]` (vertical) or `[scroll:h]` (horizontal) markers.
```bash
# Find which elements are scrollable and in which direction
winapp ui search scroll -a myapp
#   pn-scrollview-bfef Pane "scrollView" [scroll:v] (main content, vertical)
#   pn-scrollviewer-bfb1 Pane "scrollViewer" [scroll:h] (horizontal list)

# Scroll the main content down
winapp ui scroll pn-scrollview-bfef --direction down -a myapp

# Jump to top/bottom
winapp ui scroll pn-scrollview-bfef --to bottom -a myapp

# If you target an element that's not scrollable, scroll walks up to find the nearest scrollable parent
winapp ui scroll itm-someitem-a1b2 --direction down -a myapp
```

### get-focused
Show the element that currently has keyboard focus.
```bash
winapp ui get-focused -a myapp
```

### list-windows
List all visible windows for an app, including popups and dialogs.
```bash
winapp ui list-windows -a imageresizer
winapp ui list-windows -a Terminal
winapp ui list-windows                                      # all windows (no filter)
```

## Framework Support

| Framework | inspect | search | invoke | set-value | screenshot |
|---|---|---|---|---|---|
| **WPF** | ✅ Full tree | ✅ All properties | ✅ All patterns | ✅ | ✅ |
| **WinForms** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Win32** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **WinUI 3** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Electron** | ⚠️ Chromium tree | ⚠️ Limited | ⚠️ Varies | ⚠️ Varies | ✅ |
| **Flutter** | ⚠️ Basic | ⚠️ Basic | ❌ Minimal | ❌ | ✅ |

## Troubleshooting

| Error | Cause | Solution |
|---|---|---|
| "No running app found" | App not running or name mismatch | Check process name or use PID |
| "Multiple windows match" | Ambiguous `-a` value | Use `-w <HWND>` from the listed options |
| "has multiple windows" | Process has multiple windows | Use `-w <HWND>` to target specific one |
| "Selector matched N elements" | Ambiguous legacy selector | Use slugs from `inspect` output, or append `[0]`, `[1]` to legacy selectors |
| "Element may have changed" | Slug hash doesn't match current element | Re-run `inspect` or `search` to get fresh slugs |
| "does not support any invoke pattern" | Element can't be invoked | Use `inspect` on the element to find an invokable child |
| "No UIA window found" | UIA can't see the process | Use `list-windows` to find the HWND, then `-w` |
| "Window has zero size" | Window is minimized | App will be auto-restored |
| Popup/dropdown not in screenshot | PrintWindow doesn't capture overlays | Use `--capture-screen` flag |

## Common Patterns

### Navigate and verify
```bash
winapp ui invoke btn-settings-a1b2 -a myapp          # click a button
winapp ui wait-for pn-settingspage-c3d4 -a myapp    # wait for page to load
winapp ui screenshot -a myapp --output settings.png  # verify visually
```

### Find text and invoke its parent
```powershell
# Search shows invokable ancestor; invoke auto-walks to it
winapp ui invoke 'Save changes' -a myapp

# Or search first to see what matches, then invoke
winapp ui search "Save changes" -a myapp; winapp ui invoke btn-save-c3d4 -a myapp
```

### Disambiguate duplicate elements
```powershell
winapp ui search '#Image' -a myapp; winapp ui invoke itm-image-a2b3 -a myapp
```

### Screenshot with popup overlays
```powershell
winapp ui set-value txt-searchbox-e5f6 "query" -a myapp; winapp ui screenshot -a myapp --capture-screen
```

### Navigate, wait, and verify (single chain)
```powershell
winapp ui invoke btn-settings-a1b2 -a myapp; winapp ui wait-for pn-settingspage-c3d4 -a myapp --timeout 3000; winapp ui screenshot -a myapp -o settings.png
```

### Discover, click, and verify
```powershell
winapp ui inspect -a myapp --interactive; winapp ui invoke btn-submit-7a90 -a myapp; winapp ui screenshot -a myapp
```

### File dialog interaction
File open/save dialogs are standard Windows dialogs with UIA support:
```powershell
# Trigger the dialog, find it, type the path, confirm
winapp ui invoke btn-openfilebtn-a2b3 -a myapp
winapp ui list-windows -a myapp                                      # find dialog HWND
winapp ui set-value txt-1148-c4d5 "C:\path\to\file.png" -w <dialog-hwnd>
winapp ui invoke btn-open-e6f7 -w <dialog-hwnd>
```
Use `inspect -w <dialog-hwnd> --interactive` to discover the actual slugs for a specific dialog.

### Why `;` for chaining (not `&&`)
PowerShell's `&&` operator can freeze when a native CLI writes to stderr or uses ANSI escape sequences. Use `;` instead — it runs each command unconditionally and avoids this deadlock. This is also better for agent workflows: you usually want the screenshot to run even if the invoke had a non-zero exit.

## CI Testing Patterns

Use `winapp ui` commands in CI pipelines (GitHub Actions, Azure DevOps) for smoke tests
and UI validation. `wait-for` with `--property` and `--value` acts as an assertion —
it returns exit code 1 on timeout, failing the CI step automatically.

### Launch and test in GitHub Actions
```yaml
steps:
  - name: Build
    run: dotnet build MyApp.csproj -c Debug -p:Platform=x64

  - name: Launch and test
    run: |
      $result = winapp run .\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64 --detach --json | ConvertFrom-Json
      $appPid = $result.ProcessId

      # Wait for window to initialize
      winapp ui wait-for "Main Window" -a $appPid --timeout 30000

      # Run tests — each wait-for exits non-zero on failure
      winapp ui invoke "Login" -a $appPid
      winapp ui wait-for "Dashboard" -a $appPid --timeout 10000
      winapp ui screenshot -a $appPid -o dashboard.png
```

### Assert element state with `wait-for`
`wait-for --value` polls until an element's value matches the expected string, using the same
smart fallback as `get-value` (TextPattern → ValuePattern → SelectionPattern → Name). Returns exit code 0 on match,
exit code 1 on timeout — making it a CI-friendly assertion. Use `--property` to check a specific
UIA property instead.

```bash
# Assert: button click updated the counter (smart value fallback — works for TextBlock, TextBox, etc.)
winapp ui invoke "Counter Button" -a $pid
winapp ui wait-for "Counter Display" -a $pid --value "Count: 1" -t 5000

# Assert: text input was accepted
winapp ui set-value "Search Box" "hello world" -a $pid
winapp ui wait-for "Search Box" -a $pid --value "hello world" -t 3000

# Assert: checkbox was toggled (use --property for specific UIA properties)
winapp ui invoke "Dark Mode" -a $pid
winapp ui wait-for "Dark Mode" -a $pid --property ToggleState --value "On" -t 3000

# Assert: navigation happened (new page appeared)
winapp ui invoke "Settings" -a $pid
winapp ui wait-for "Settings Page" -a $pid -t 10000

# Assert: dialog was dismissed (element disappeared)
winapp ui invoke "Close" -a $pid
winapp ui wait-for "Dialog Title" -a $pid --gone -t 5000
```

### Assert with JSON output
Use `--json` with PowerShell or jq for more complex assertions:
```powershell
# Assert: search found exactly one match
$result = winapp ui search "Submit" -a $pid --json | ConvertFrom-Json
if ($result.matchCount -ne 1) { throw "Expected 1 Submit button, found $($result.matchCount)" }

# Assert: element has expected properties
$tree = winapp ui inspect "Counter Display" -a $pid --json | ConvertFrom-Json
if ($tree.elements[0].name -ne "Count: 3") { throw "Counter value wrong: $($tree.elements[0].name)" }
```

### Full smoke test example
```powershell
# Launch
$app = winapp run .\build-output --detach --json | ConvertFrom-Json

# Verify app loaded
winapp ui wait-for "Main Page" -a $app.ProcessId -t 30000

# Interact and assert
winapp ui invoke "Add Item" -a $app.ProcessId
winapp ui set-value "Item Name" "Test Item" -a $app.ProcessId
winapp ui invoke "Save" -a $app.ProcessId
winapp ui wait-for "Test Item" -a $app.ProcessId -t 5000              # assert item appeared in list
winapp ui wait-for "Save" -a $app.ProcessId --gone -t 3000            # assert save dialog closed

# Visual verification
winapp ui screenshot -a $app.ProcessId -o smoke-test.png
```
