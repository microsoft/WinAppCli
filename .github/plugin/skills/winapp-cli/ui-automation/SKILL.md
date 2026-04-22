---
name: winapp-ui-automation
description: Inspect and interact with running Windows app UIs from the command line using UI Automation (UIA). Use when an AI agent or developer needs to inspect a UI element tree, find controls, take screenshots, click buttons, read or set text, or verify UI state in a running Windows app. Works with any framework: WinUI 3, WPF, WinForms, Win32, Electron.
version: 0.3.1
---
## When to use
- Inspecting a running Windows app's UI from the command line
- AI agents interacting with Windows applications (clicking buttons, reading text, taking screenshots)
- Verifying UI state during development or testing
- Automating UI workflows without Playwright or Selenium
- Debugging WinUI 3, WPF, WinForms, Win32, or Electron app UIs

## Prerequisites
- For UIA mode (any app): No setup needed — works with any running Windows app

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
```

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
- Use `--capture-screen` to capture popup overlays, dropdown menus, and flyouts
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

# 3. Target the dialog, type the file path, and confirm
winapp ui set-value txt-1148-c4d5 "C:\path\to\file.png" -w <dialog-hwnd>
winapp ui invoke btn-open-e6f7 -w <dialog-hwnd>
```
Note: The filename input in standard file dialogs typically has AutomationId `1148`. Use `inspect -w <dialog-hwnd> --interactive` to discover the actual slugs.

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
| Popup not in screenshot | PrintWindow misses overlays | Use `--capture-screen` flag |


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
| `--capture-screen` | Capture from screen (includes popups/overlays) instead of window rendering. Brings window to foreground first. | (none) |
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

### `winapp ui set-value`

Set a value on an element using UIA ValuePattern. Works for TextBox, ComboBox, Slider, and other editable controls. Usage: winapp ui set-value <selector> <value> -a <app>

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

Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, or --to to jump to top/bottom.

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

### `winapp ui get-focused`

Show the element that currently has keyboard focus in the target app.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |
