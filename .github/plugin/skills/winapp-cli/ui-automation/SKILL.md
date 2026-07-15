---
name: winapp-ui-automation
description: Inspect and interact with running Windows app UIs from the command line using UI Automation (UIA). Use when an AI agent or developer needs to inspect a UI element tree, find controls, take screenshots, click buttons, read or set text, or verify UI state in a running Windows app. Works with any framework WinUI 3, WPF, WinForms, Win32, Electron.
version: 0.4.1
---
## When to use
- Inspecting a running Windows app's UI from the command line
- AI agents interacting with Windows applications (clicking buttons, reading text, taking screenshots)
- Verifying UI state during development or testing
- Automating UI workflows without Playwright or Selenium
- Debugging WinUI 3, WPF, WinForms, Win32, or Electron app UIs

## Prerequisites
- For UIA mode (any app): No setup needed — works with any running Windows app
- For input-injecting verbs (`click`, `hover`, `drag`, `touch`, `pen`, `scroll --wheel`, `send-keys --via send-input`): an **unlocked, interactive desktop** with the target window foregroundable. On a locked/secure desktop they fail fast with `no_interactive_desktop`. The UIA-pattern verbs (`inspect`, `search`, `get-*`, `wait-for`, `set-value`, `invoke`, `scroll --direction/--to`, `screenshot`) are headless/locked-session friendly — prefer them in CI.

## Common patterns

### Discover and interact
```powershell
# See what's clickable, then screenshot for context
winapp ui inspect -a myapp --interactive; winapp ui screenshot -a myapp

# Click and verify the page changed
winapp ui invoke btn-settings-a1b2 -a myapp; winapp ui wait-for pn-settingspage-c3d4 -a myapp --timeout 3000; winapp ui screenshot -a myapp

# Fill a form and submit
winapp ui set-value txt-searchbox-e5f6 "hello" -a myapp; winapp ui invoke btn-submit-7a90 -a myapp; winapp ui screenshot -a myapp
```

### Find visible text and click it
```powershell
# Search by text — output shows invokable ancestor
winapp ui search "Save changes" -a myapp
# Output:
#   lbl-savechanges-a1b2 "Save changes" (120,40 80x20)
#         ^ invoke via: btn-save-c3d4 "Save"

# Invoke by text — auto-walks to parent Button
winapp ui invoke 'Save changes' -a myapp
```

### Navigate multi-page apps
```powershell
# Click nav item, wait for page, inspect what's available
winapp ui invoke itm-samples-3f2c -a myapp; winapp ui wait-for pn-samplespage-b4e7 -a myapp; winapp ui inspect -a myapp --interactive
```

### Disambiguate duplicate elements
```powershell
# When text search matches multiple elements, the error shows slugs for each — pick the right one
winapp ui invoke Submit -a myapp
# → Selector matched 3 elements:
#   [0] Button "Submit Order" → btn-submitorder-a1b2
#   [1] Button "Submit" → btn-submit-c3d4
# Use the slug: winapp ui invoke btn-submit-c3d4 -a myapp
```

## Key concepts
- **Selector brackets**: `inspect` and `search` output shows selectors in `[brackets]` — use the bracketed value with other `ui` commands. Selectors are either AutomationId (stable, developer-set) or generated slug (e.g., `btn-name-hash`).
- **AutomationId selectors**: When an element has a unique AutomationId, it becomes the selector directly (e.g., `[MinimizeButton]`). These survive layout changes and localization — preferred for stable targeting.
- **Slug selectors**: When no unique AutomationId exists, a generated slug is used (e.g., `[btn-close-a2b3]`). Format: `prefix-name-hash`. May go stale after UI changes.
- **Plain text search**: `search` and `invoke` accept plain text — `search Minimize` finds elements with "Minimize" in their Name or AutomationId (substring, case-insensitive). No special syntax needed.
- **`--interactive` flag**: Filters to invokable elements only with auto-depth 8 — the fastest way to see what you can click
- **Invokable ancestor surfacing**: When a search result isn't invokable, the nearest invokable parent is shown with its selector
- **`;` chaining**: Chain commands with `;` to run multiple operations in one call, reducing agent round-trips
- **`-a` vs `-w`**: Use `-a` to find apps by name/title/PID. Use `-w <HWND>` for stable window targeting
- **Element markers**: `[on]`/`[off]` for toggles, `[collapsed]`/`[expanded]`, `[scroll:v]`/`[scroll:h]`/`[scroll:vh]` for scrollable containers, `[offscreen]`, `[disabled]`, `value="..."` for editable elements

## Usage

### Connect and discover
```powershell
# Connect and see interactive elements in one call
winapp ui status -a myapp; winapp ui inspect -a myapp --interactive
```

### Inspect element tree
```powershell
winapp ui inspect -a myapp --interactive      # invokable elements only, auto-depth 8
winapp ui inspect -a myapp --depth 5          # deeper tree at depth 5
winapp ui inspect txt-searchbox-e5f6 -a myapp  # subtree rooted at element
winapp ui inspect btn-settings-a1b2 -a myapp --ancestors  # walk up from element to root
winapp ui inspect -a myapp --hide-offscreen   # hide offscreen elements
```

### Find elements
```powershell
winapp ui search Close -a myapp               # finds elements with "Close" in name or automationId
winapp ui search Button -a myapp              # finds elements with "Button" in name (also matches type names)
winapp ui search image -a myapp               # case-insensitive substring match
```

### Screenshot
```powershell
# Full window screenshot
winapp ui screenshot -a myapp --output page.png

# Crop to element; capture with popups visible
winapp ui screenshot txt-searchbox-e5f6 -a myapp --output search.png
winapp ui screenshot -a myapp --capture-screen --output with-popups.png

# Bring window to foreground first (matches what the user is currently seeing)
winapp ui screenshot -a myapp --focus --output focused.png
```

### Hover (for tooltips, flyouts, hover states)
`--dwell-time <ms>` sets how long to wait after hovering (default: 800, range: 0–10000).
```powershell
# Hover to trigger tooltip, then capture it (default 800ms dwell)
winapp ui hover btn-info-a1b2 -a myapp; winapp ui screenshot -a myapp --capture-screen --output tooltip.png

# Longer dwell for apps with slow tooltip timers
winapp ui hover btn-info-a1b2 -a myapp --dwell-time 1200; winapp ui screenshot -a myapp --capture-screen
```

### Send keyboard input
Synthesize keystrokes — the keyboard counterpart to `click`. Use for arrow/Tab/Enter navigation, shortcuts, and per-keystroke typing (vs `set-value`'s atomic write). Tokens are whitespace-separated: named keys (`enter`, `down`, `tab`, `esc`, `f5`), modifier combos (`ctrl+shift+t`), literal text (`hello`), and raw virtual keys (`vk=0xNN`).
```powershell
# Keyboard navigation then commit
winapp ui send-keys "down down enter" -a myapp

# Type the literal words "down down enter" instead of pressing those keys (text= escapes each token)
winapp ui send-keys "text=down text=down text=enter" -a myapp

# Same intent, less typing: --verbatim types the whole argument literally (and keeps exact whitespace)
winapp ui send-keys "down down enter" -a myapp --verbatim

# Shortcut: select all and delete
winapp ui send-keys "ctrl+a delete" -a myapp

# Focus a field, then type text into it
winapp ui send-keys "Hello world" --target txt-name-a1b2 -a myapp

# Transport: --via post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide)
winapp ui send-keys "enter" -a myapp --via send-input

# Fire a global hotkey: win+... is refused by default (acts on the shell); opt in with --allow-system-keys
winapp ui send-keys "win+shift+v" -a myapp --via send-input --allow-system-keys
```
- Default `post-message` is HWND-targeted and works across integrity levels, but can't fire `WH_KEYBOARD_LL` global hotkeys; for classic Win32/WinForms child-window controls, target the control with `-w`/`--target`.
- A token that collides with a key/modifier name (e.g. `enter`, `down`, `ctrl+a`) is pressed as that key. Prefix it with `text=` to type it as literal text instead — `text=enter` types the word "enter"; chain `text=` tokens to type a literal phrase like `text=down text=down text=enter`. Backslash escapes inside a `text=` value type whitespace the tokenizer would otherwise collapse: `\s`→space, `\t`→tab, `\n`→newline, `\r`→CR, `\\`→backslash (e.g. `text=a\s\sb` → "a  b"). When the *whole* argument is literal text, pass `--verbatim` instead of escaping each token: it types the entire keys argument as-is (no key/combo/`vk=`/`text=` parsing) and preserves exact whitespace — `send-keys "down down enter" --verbatim` types the words. (`--verbatim` does not decode backslash escapes; use a `text=` token for control characters.)
- `send-input` is fully real input but goes to the foreground window and is UIPI-blocked when injecting from elevated → AppContainer/AppX. It **rejects system-reserved combos** (`win+l`, `alt+f4`, `ctrl+shift+esc`, `ctrl+alt+del`, `alt+tab`, …) because those act on the OS/shell, not just the target — pass **`--allow-system-keys`** to opt in (e.g. to fire a global hotkey such as PowerToys' `win+shift+v` or `win+r`), or use `--via post-message` (window-scoped) to send one straight to the window. **`win+l` stays blocked even with `--allow-system-keys`** — it locks the workstation via `LockWorkStation()` (unrecoverable from automation). Windows still blocks secure sequences like `ctrl+alt+del` (SAS) from injected input regardless of the flag. On a locked/secure desktop `send-input` fails fast with `no_interactive_desktop`.
- Per-keystroke events: named keys/combos fire a real `KeyDown` on both transports. For literal typed text, `--via send-input` maps each char to its VK (+Shift) so each character fires a real `KeyDown` + OS-composed `WM_CHAR` (`TextChanged`) — use it when downstream logic keys off `KeyDown` (e.g. WinUI 3/WPF `TextBox`); bring the target window to the foreground first. `--via post-message` posts `WM_CHAR` (raises `TextChanged`, lands correct text across integrity levels) but does not fire a per-character `KeyDown`.

### Drag (reorder, resize, sliders, drag-and-drop)
Press the mouse button at one point, move to another, then release with `drag <from> <to>`, where each endpoint is an element selector (uses its center) or app `x,y` coordinates from `ui inspect`. Uses `SendInput` with intermediate moves so apps see a realistic `WM_MOUSEMOVE` stream.
```powershell
# Reorder one item onto another (center → center)
winapp ui drag itm-card-9f8e itm-slot-2c1a -a myapp

# Element center → app coordinates (as reported by `ui inspect`)
winapp ui drag itm-card-9f8e 300,400 -a myapp

# Raw app coordinates → app coordinates
winapp ui drag 120,200 480,200 -a myapp

# Right-button drag
winapp ui drag itm-card-9f8e itm-trash-0001 -a myapp --right
```
- A selector drags from/to the element's center; `x,y` are app coordinates in the same space `ui inspect`/`search` report. Element endpoints are re-resolved just before the drag and fail with `target_moved` if still animating; on a locked/secure desktop the drag fails with `no_interactive_desktop`.

### Touch gestures (tap, swipe, pinch, stretch, long-press)
Inject synthetic touch. The contact anchor is an element selector (its center) or an explicit `--at x,y` app coordinate. Prefers the modern synthetic-pointer device and falls back to the legacy touch-injection API.
```powershell
# Tap an element center; or tap explicit app coordinates
winapp ui touch btn-ok-1a2b -a myapp
winapp ui touch -a myapp --at 320,240

# Long-press, swipe, and two-finger pinch/stretch (zoom)
winapp ui touch tile-photo-7b3c -a myapp --gesture long-press --hold-ms 600
winapp ui touch -a myapp --at 100,300 --gesture swipe --to-point 400,300
winapp ui touch img-map-9f8e -a myapp --gesture pinch --distance 200
winapp ui touch img-map-9f8e -a myapp --gesture stretch --distance 200
```
- Gestures: `tap` (default), `double-tap`, `long-press`, `swipe`, `pinch`, `stretch`. `--fingers` 1–10 (pinch/stretch always 2). Refuses without a non-zero foregrounded target (`no_target`/`foreground_not_target`/`no_interactive_desktop`); every coordinate is bounds-checked against the target window and rejected with `invalid_arguments` if outside. If injected touch is unsupported on the device, the command surfaces the real Win32 error rather than a false success. In a Remote Desktop/VM session delivery isn't guaranteed even on exit 0 — the command appends a delivery-uncertainty warning (`warnings[]` in `--json`); verify the effect with a screenshot.

### Pen / stylus (taps and ink strokes)
Inject synthetic pen input (Windows 10 1809+). Target an element center, an explicit `--at`, or a full `--path` ink stroke, with pressure/tilt/eraser control.
```powershell
# Pen tap at element center; firm tap at explicit coords
winapp ui pen canvas-1a2b -a myapp
winapp ui pen -a myapp --at 320,240 --pressure 0.8

# Draw an ink stroke, or erase along one
winapp ui pen -a myapp --path "100,100 150,120 210,140 260,120"
winapp ui pen -a myapp --path "100,100 260,100" --eraser
```
- `--pressure` 0.0–1.0, `--tilt-x`/`--tilt-y` ±90°, `--eraser` for the eraser end. Same injection safety as `touch`: requires a non-zero foregrounded target window and bounds-checks every ink point (out-of-bounds → `invalid_arguments`, nothing injected). Pen routing is especially unreliable over Remote Desktop — exit 0 can mean the call succeeded but no pen reached the app; the command appends a delivery-uncertainty warning (`warnings[]` in `--json`). Validate pen flows on a local interactive desktop.

### Read element state
```powershell
# Read text/value content (works for RichEditBox, TextBox, ComboBox, Slider, labels)
winapp ui get-value doc-texteditor-53ad -a notepad
winapp ui get-value SearchBox -a myapp
winapp ui get-value CmbTheme -a myapp              # reads ComboBox selected item via SelectionPattern

# Check toggle/selection state, value, scroll position
winapp ui get-property chk-agreecheckbox-b2c3 -a myapp --property ToggleState
winapp ui get-property txt-textbox-a4b1 -a myapp --property Value
winapp ui get-property cmb-modellist-d5e6 -a myapp --property IsSelected

# See what has keyboard focus
winapp ui get-focused -a myapp
```

### Set values
`set-value` writes programmatically (no keystrokes, no foreground) via a fallback chain: ValuePattern → RangeValuePattern (numeric) → LegacyIAccessible `put_accValue` for TextPattern-only edit controls.
```powershell
winapp ui set-value txt-searchbox-e5f6 "hello" -a myapp        # TextBox/ComboBox via ValuePattern
winapp ui set-value sld-volume-b2c3 75 -a myapp                # Slider via RangeValuePattern
winapp ui set-value doc-compose-9f3a "hello" -a myapp          # RichEdit/compose box via LegacyIAccessible
```
- The LegacyIAccessible fallback reaches rich-edit/compose controls that expose no ValuePattern, as long as their accessibility implements `put_accValue` (native Win32 rich-edit and Chromium/Electron/WebView2 compose boxes typically do).
- **WinUI 3 `RichEditBox` and WPF `RichTextBox` don't support programmatic value-setting** — by design they're read-only to UI Automation's value APIs (Text pattern, no settable Value pattern). `set-value` fails on them with a clear error — use `send-keys` (needs an unlocked, foregrounded desktop) to type into those instead. Note `get-value` can still *read* them via TextPattern.

### Scroll containers
```powershell
# Find scrollable containers — look for [scroll:v] (vertical) or [scroll:h] (horizontal)
winapp ui search scroll -a myapp
# Output:
#   pn-scrollview-bfef Pane "scrollView" [scroll:v] (2127,296 1191x965)
#   pn-scrollviewer-bfb1 Pane "scrollViewer" [scroll:h] (2127,296 1191x216)

# Scroll vertically
winapp ui scroll pn-scrollview-bfef --direction down -a myapp

# Scroll to top/bottom
winapp ui scroll pn-scrollview-bfef --to bottom -a myapp

# Scroll and then inspect for newly visible elements
winapp ui scroll pn-scrollview-bfef --direction down -a myapp; winapp ui search TargetItem -a myapp

# Synthesize real mouse-wheel input over the element (1 = one notch up, -1 = down) — tests wheel handlers (zoom, custom scroll)
winapp ui scroll img-map-a1b2 --wheel -1 -a myapp
```

### Wait for UI state
```powershell
winapp ui wait-for btn-submit-a1b2 -a myapp --timeout 5000
winapp ui wait-for itm-status-c3d4 -a myapp --value "Complete" --timeout 5000
```

## Tips
- Use `--interactive` with `inspect` as your first command — it shows only what you can click
- Chain commands with `;` to reduce round-trips (see note below on why not `&&`)
- Use slugs from output to target specific elements — they're hash-validated and shell-safe
- Use plain text search to find elements: `search Minimize`, `invoke Submit`
- When multiple elements match text search, the error shows slugs for each — pick the right one
- Use `get-property --property ToggleState` to verify checkbox/toggle state after invoke
- `scroll` auto-finds the nearest scrollable parent
- Use `--capture-screen` to capture popup overlays, dropdown menus, and flyouts (also brings the window to the foreground)
- Use `hover` before `screenshot --capture-screen` to capture tooltips and hover-triggered UI
- Use `--focus` to foreground the target window before capture without switching to screen-DC capture (default capture path uses Windows.Graphics.Capture and works while occluded)
- Use `--hide-disabled` and `--hide-offscreen` to reduce noise

### Why `;` instead of `&&`
Use `;` (not `&&`) to chain commands. PowerShell's `&&` operator can freeze when a native CLI writes to stderr or uses ANSI escape sequences — this causes a pipeline deadlock. `;` runs each command unconditionally and avoids this issue. This is also better for agent workflows: you usually want the screenshot to run even if the invoke had a non-zero exit (to see what went wrong).

### File dialog workaround
File open/save dialogs are standard Windows dialogs with UIA support. Interact with them using existing commands:
```powershell
# 1. Trigger the dialog (e.g., click "Open File" button)
winapp ui invoke btn-openfilebtn-a2b3 -a myapp

# 2. Find the dialog window
winapp ui list-windows -a myapp
# → Shows the main window + the dialog HWND
# Note: untitled zero-size windows are hidden by default; use --show-hidden to include them

# 3. Target the dialog, type the file path, and confirm
winapp ui set-value txt-1148-c4d5 "C:\path\to\file.png" -w <dialog-hwnd>
winapp ui invoke btn-open-e6f7 -w <dialog-hwnd>
```
Note: The filename input in standard file dialogs typically has AutomationId `1148`. Use `inspect -w <dialog-hwnd> --interactive` to discover the actual slugs.

## JSON output envelopes (v0.3.1+)

The `--json` envelope for `ui inspect`, `ui get-focused`, `ui search`, and `ui wait-for` was reshaped in v0.3.1. Pre-0.3.1 parsers will silently break — most fields were renamed, removed, or moved into envelopes. Highlights:

- `ui inspect --json` now nests elements under `windows[].elements[]` (was a flat `elements[]`).
- `ui get-focused --json` always emits an envelope — `{ "hasFocus": false }` or `{ "hasFocus": true, "element": {...} }` (was bare `null`).
- `ui search --json` / `ui wait-for --json` may include an `invokableAncestor` field (element-shaped) on each match.
- Per-element `id`, `parentSelector`, and `windowHandle` are **removed** — use `selector` as the public handle.

Full schemas with examples: `references/ui-json-envelope.md`.

## Related skills
- `winapp-setup` for adding Windows SDK to your project
- `winapp-package` for packaging apps as MSIX

## Troubleshooting
| Error | Cause | Solution |
|---|---|---|
| "No running app found" | Wrong name or app not running | Try process name, window title, or PID |
| "Multiple windows match" | Several windows match `-a` | Use `-w <HWND>` from the listed options |
| "Selector matched N elements" | Text query matches multiple elements | Use a slug from the suggestions shown in the error, or from `inspect` output |
| "Element may have changed" | Slug hash doesn't match current element | Re-run `inspect` to get fresh slugs |
| "does not support any invoke pattern" | Element can't be invoked | The error shows the invokable ancestor slug if one exists — use that |
| "No UIA window found" | UIA can't see the window | Use `list-windows` to find HWND, then `-w` |
| Popup not in screenshot | Default capture path doesn't include unowned overlays | Use `--capture-screen` flag |


## Command Reference

### `winapp ui status`

Connect to a target app and display connection info.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui inspect`

View the UI element tree with semantic slugs, element types, names, and bounds.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--ancestors` | Walk up the tree from the specified element to the root | (none) |
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--depth` | Tree inspection depth | `4` |
| `--hide-disabled` | Hide disabled elements from output | (none) |
| `--hide-offscreen` | Hide offscreen elements from output | (none) |
| `--interactive` | Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui search`

Search the element tree for elements matching a text query. Returns all matches with semantic slugs.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--max` | Maximum search results | `50` |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui get-property`

Read UIA property values from an element. Specify --property for a single property or omit for all.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--property` | Property name to read or filter on | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui get-value`

Read the current value from an element. Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). Usage: winapp ui get-value <selector> -a <app>

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui screenshot`

Capture the target window or element as a PNG image. When multiple windows exist (e.g., dialogs), captures each to a separate file. With --json, returns file path and dimensions. Use --capture-screen for popup overlays.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--capture-screen` | Capture from screen DC via BitBlt (includes popups/overlays not owned by the target). Implies --focus. | (none) |
| `--focus` | Bring the target window to the foreground before capture. Already implied by --capture-screen. | (none) |
| `--json` | Format output as JSON | (none) |
| `--output` | Save output to file path (e.g., screenshot) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui invoke`

Activate an element by slug or text search. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui click`

Click an element by slug or text search using mouse simulation. Works on elements that don't support InvokePattern (e.g., column headers, list items). Use --double for double-click, --right for right-click.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--double` | Perform a double-click instead of a single click | (none) |
| `--json` | Format output as JSON | (none) |
| `--right` | Perform a right-click instead of a left click | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui drag`

Press the mouse button at one point, move to another, then release. 'drag <from> <to>', where <from>/<to> are each an element selector (uses the element's center) or app-relative x,y coordinates as reported by 'ui inspect'. Useful for reorder/resize/slider gestures and drag-and-drop. Use --right for a right-button drag, --hold-ms for press-and-hold/long-press, and --dwell-ms to settle on a drop target before releasing.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<from>` | No | Start point — an element selector (drags from its center) or app coordinates x,y as reported by 'ui inspect' (e.g. pn-list-d736 or 100,200). |
| `<to>` | No | End point — an element selector (drops at its center) or app coordinates x,y as reported by 'ui inspect' (e.g. pn-target-d746 or 300,400). |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--dwell-ms` | Milliseconds to dwell at the destination after moving, before releasing (default: 0). Lets drop targets / merge overlays that arm from a sustained hover latch before release. | (none) |
| `--hold-ms` | Milliseconds to hold the button down at the start before moving (default: 0). With <from> == <to> (no movement) this performs a press-and-hold / long-press gesture. | (none) |
| `--json` | Format output as JSON | (none) |
| `--right` | Drag with the right mouse button instead of the left button | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui touch`

Inject synthetic touch input using the Windows touch-injection API. Supports tap, double-tap, long-press, swipe, pinch and stretch gestures at an element's center or explicit app x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--at` | Explicit start point as app coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. | (none) |
| `--direction` | Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given. | `right` |
| `--distance` | Distance in pixels for pinch/stretch (finger spread) or swipe. | (none) |
| `--duration-ms` | Glide time in milliseconds for moving gestures (swipe/pinch/stretch). | `300` |
| `--fingers` | Number of touch contacts (default: 1). Pinch/stretch always use 2. | `1` |
| `--gesture` | Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap). | `tap` |
| `--hold-ms` | Milliseconds to hold contacts down before lifting (long-press hold time). Defaults to 500 ms when --gesture long-press is used and this option is not set. | (none) |
| `--json` | Format output as JSON | (none) |
| `--to-point` | End point x,y for a swipe (app coordinates). Takes precedence over --direction. | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui pen`

Inject synthetic pen/stylus input using the Windows synthetic-pointer API. Taps or draws ink strokes with configurable pressure, tilt and eraser mode, at an element's center or explicit app x,y coordinates. Requires an unlocked, interactive desktop with the target window foregroundable (Windows 10 1809+).

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--at` | Pen contact point as app coordinates x,y (as reported by 'ui inspect'). Defaults to the selector's element center. Ignored when --path is given. | (none) |
| `--duration-ms` | Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment). | (none) |
| `--eraser` | Use the eraser end of the pen instead of the tip. | (none) |
| `--json` | Format output as JSON | (none) |
| `--path` | Ink stroke path as a whitespace-separated list of x,y pairs, e.g. "10,10 20,30 40,50". | (none) |
| `--pressure` | Pen pressure from 0.0 to 1.0 (default: 0.5). | `0.5` |
| `--tilt-x` | Pen tilt along the x-axis in degrees (-90 to 90, default: 0). | (none) |
| `--tilt-y` | Pen tilt along the y-axis in degrees (-90 to 90, default: 0). | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui hover`

Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). Uses SendInput for realistic mouse movement and waits for a configurable dwell time.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--dwell-time` | Time in milliseconds to wait after hovering for hover effects to appear (default: 800) | `800` |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui send-keys`

Send synthetic keyboard input to a window. Supports named keys (down, enter, tab), modifier combos (ctrl+shift+t), raw virtual keys (vk=0xNN), and literal text. Use --verbatim to type the whole argument literally, or --target to focus an element first. Two transports via --via: post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide). For per-keystroke KeyDown on typed text (e.g. a WinUI 3/WPF TextBox), use --via send-input.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<keys>` | No | Keys to send. Whitespace-separated tokens: named keys (down, enter, tab, esc, f5), modifier combos (ctrl+shift+t, alt+f4), raw virtual keys (vk=0x42), or literal text (hello). Use text=<literal> to type a single value verbatim when it would otherwise be read as a key name or combo (text=enter types "enter"; text=ctrl+a types "ctrl+a"); backslash escapes \s \t \n \r \\ are supported (text=a\s\sb types "a  b"). To type the whole argument literally without escaping each token, pass --verbatim instead. Quote multi-token strings, e.g. "ctrl+a delete". |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--allow-system-keys` | Allow synthesizing system-/shell-reserved combos (win+<key>, alt+f4, alt+tab, ctrl+esc, …) via --via send-input, which are refused by default because they act on the OS/shell beyond the target app. Opt in to drive global hotkeys (e.g. PowerToys' win+shift+v, win+r). No effect on --via post-message (already window-scoped; a warning is emitted if set without send-input). Note: win+l stays blocked even with this flag — it locks the workstation (LockWorkStation() via the shell hook), which is unrecoverable from automation. Windows still blocks secure sequences such as ctrl+alt+del (SAS) from injected input regardless of this flag. | (none) |
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--target` | Optional selector (slug or text) to focus before sending keys. | (none) |
| `--verbatim` | Type the entire keys argument as literal text — no named-key, combo, or vk= interpretation, and exact whitespace preserved. The whole-argument form of the per-token text= escape: --verbatim "down down enter" types the words instead of pressing Down, Down, Enter. | (none) |
| `--via` | Transport: post-message (default, HWND-targeted, bypasses UIPI; typed text raises TextChanged but not a per-character KeyDown) or send-input (OS-wide; typed text raises a real per-character KeyDown + TextChanged). Named keys and combos raise KeyDown on both, but keyboard accelerators/shortcuts (KeyboardAccelerator, e.g. ctrl+t) only fire via send-input. | `post-message` |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui set-value`

Set a value on an element programmatically. Works for TextBox, ComboBox, Slider, and other editable controls via UIA ValuePattern/RangeValuePattern, with a LegacyIAccessible (put_accValue) fallback for TextPattern-only edit controls — no app foreground required. Some rich text controls (e.g. WinUI 3 RichEditBox and WPF RichTextBox) don't support setting their value programmatically — use the 'send-keys' command with '--via send-input' to type into them instead. Usage: winapp ui set-value <selector> <value> -a <app>

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |
| `<value>` | No | Value to set (text for TextBox/ComboBox, number for Slider) |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui focus`

Move keyboard focus to the specified element using UIA SetFocus.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui scroll-into-view`

Scroll the specified element into the visible area using UIA ScrollItemPattern.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui scroll`

Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, --to to jump to top/bottom, or --wheel to synthesize mouse-wheel input.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--direction` | Scroll direction: up, down, left, right | (none) |
| `--json` | Format output as JSON | (none) |
| `--to` | Scroll to position: top, bottom | (none) |
| `--wheel` | Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). Synthesizes real wheel input instead of using ScrollPattern. | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui wait-for`

Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--contains` | Use substring matching for --value instead of exact match | (none) |
| `--gone` | Wait for element to disappear instead of appear | (none) |
| `--json` | Format output as JSON | (none) |
| `--property` | Property name to read or filter on | (none) |
| `--timeout` | Timeout in milliseconds | `5000` |
| `--value` | Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead. | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui list-windows`

List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--show-hidden` | Include untitled zero-size windows that are hidden by default | (none) |

### `winapp ui get-focused`

Show the element that currently has keyboard focus in the target app.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |
