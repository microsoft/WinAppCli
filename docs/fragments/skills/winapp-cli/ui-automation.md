## When to use
- Inspecting a running Windows app's UI from the command line
- AI agents interacting with Windows applications (clicking buttons, reading text, taking screenshots)
- Verifying UI state during development or testing
- Automating UI workflows without Playwright or Selenium
- Debugging WinUI 3, WPF, WinForms, Win32, or Electron app UIs

## Prerequisites
- For UIA mode (any app): No setup needed — works with any running Windows app
- For input-injecting verbs (`click`, `hover`, `drag`, `scroll --wheel`, `send-keys --via send-input`): an **unlocked, interactive desktop** with the target window foregroundable. On a locked/secure desktop they fail fast with `no_interactive_desktop`. The UIA-pattern verbs (`inspect`, `search`, `get-*`, `wait-for`, `set-value`, `invoke`, `scroll --direction/--to`, `screenshot`) are headless/locked-session friendly — prefer them in CI.

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
```
- Default `post-message` is HWND-targeted and works across integrity levels, but can't fire `WH_KEYBOARD_LL` global hotkeys; for classic Win32/WinForms child-window controls, target the control with `-w`/`--target`.
- A token that collides with a key/modifier name (e.g. `enter`, `down`, `ctrl+a`) is pressed as that key. Prefix it with `text=` to type it as literal text instead — `text=enter` types the word "enter"; chain `text=` tokens to type a literal phrase like `text=down text=down text=enter`. Backslash escapes inside a `text=` value type whitespace the tokenizer would otherwise collapse: `\s`→space, `\t`→tab, `\n`→newline, `\r`→CR, `\\`→backslash (e.g. `text=a\s\sb` → "a  b"). When the *whole* argument is literal text, pass `--verbatim` instead of escaping each token: it types the entire keys argument as-is (no key/combo/`vk=`/`text=` parsing) and preserves exact whitespace — `send-keys "down down enter" --verbatim` types the words. (`--verbatim` does not decode backslash escapes; use a `text=` token for control characters.)
- `send-input` is fully real input but goes to the foreground window and is UIPI-blocked when injecting from elevated → AppContainer/AppX. It **rejects system-reserved combos** (`win+l`, `alt+f4`, `ctrl+shift+esc`, `ctrl+alt+del`, `alt+tab`, …) because those act on the OS/shell, not just the target — use `--via post-message` (window-scoped) if you really need to send one to the window. On a locked/secure desktop `send-input` fails fast with `no_interactive_desktop`.
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
