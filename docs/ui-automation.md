# UI Automation

Inspect and interact with running Windows applications from the command line.
Used by AI agents and developers for UI testing, debugging, and automation.

## Overview

`winapp ui` provides commands for inspecting and interacting with Windows app UIs.
Two modes are available, auto-detected per session:

- **UIA mode** — Works with any Windows app (WPF, WinForms, Win32, Electron, WinUI 3).
  Uses Windows UI Automation. Safe by design — no global input injection.
- **DevTools mode** — For WinUI 3 apps with the DevTools assembly. Provides full XAML tree
  access, property writing, input simulation, hot-reload, and more. (Future)

## Quick Start

```bash
# Connect to any app and see its UI tree
winapp ui inspect -a notepad

# Find specific elements
winapp ui search Button -a notepad

# Activate an element
winapp ui invoke "#Close" -a notepad

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

Target elements by ID, name, AutomationId, type, or text content:

| Selector | Meaning | Example |
|---|---|---|
| `e5` | Element ID (from last inspect/search) | `winapp ui invoke e5 -a notepad` |
| `#Submit` | Name="Submit" (exact) | `winapp ui invoke "#Submit" -a myapp` |
| `$SearchBox` | AutomationId="SearchBox" | `winapp ui invoke '$SearchBox' -a myapp` |
| `Button` | Type=Button | `winapp ui search Button -a myapp` |
| `Button#OK` | Type + Name | `winapp ui invoke "Button#OK" -a myapp` |
| `~hello` | Text contains "hello" (case-insensitive) | `winapp ui search "~hello" -a myapp` |

Element IDs are assigned by the last `inspect` or `search` command. They are valid until the next `inspect`/`search` overwrites them.

### Text content search (`~`)

Search for elements whose visible text contains a substring:

```bash
winapp ui search "~Save" -a notepad        # find elements containing "Save"
winapp ui search "~error" -a myapp          # case-insensitive match
```

For text matches on non-invokable elements (e.g., a TextBlock inside a Button), the search
also surfaces the nearest **invokable ancestor** — the parent element you can use with `invoke`:

```
  e3  Text "Save changes"  (120,40 80x20)
        ↑ invoke via: e2  Button "Save"
```

The invokable ancestor's element ID is cached, so you can immediately use it:
```bash
winapp ui invoke e2 -a myapp    # invoke the parent Button
```

## Commands

### status
Connect to an app and show connection info.
```bash
winapp ui status -a notepad
winapp ui status -a notepad --json
```

### inspect
View the UI element tree.
```bash
winapp ui inspect -a notepad                    # default depth 3
winapp ui inspect -a notepad --depth 5          # deeper
winapp ui inspect --ancestors e5 -a notepad     # walk up from element
```

### search
Find elements matching a selector.
```bash
winapp ui search Button -a notepad              # all buttons
winapp ui search "#Close" -a notepad            # by name
winapp ui search '$SearchBox' -a notepad        # by AutomationId
winapp ui search Button --max 10 -a notepad     # limit results
```

### get-property
Read property values from an element.
```bash
winapp ui get-property e5 -a notepad            # all properties
winapp ui get-property e5 -p IsEnabled -a notepad  # specific property
```

### screenshot
Capture a window or element as PNG.
```bash
winapp ui screenshot -a notepad                     # saves screenshot.png in cwd
winapp ui screenshot -a notepad --output my.png     # custom filename
winapp ui screenshot -a notepad --json              # returns file path as JSON
winapp ui screenshot -w 131906                      # target specific HWND
```

### invoke
Programmatically activate an element (click button, toggle checkbox, expand combo box).
```bash
winapp ui invoke "#Submit" -a myapp             # by name
winapp ui invoke e5 -a myapp                    # by ID
winapp ui invoke '$SizeComboBox' -a myapp       # expand combo box
```

Tries patterns in order: InvokePattern → TogglePattern → SelectionItemPattern → ExpandCollapsePattern.

### set-value
Set text on an editable element.
```bash
winapp ui set-value e2 --text "Hello world" -a notepad
```

### focus
Move keyboard focus to an element.
```bash
winapp ui focus e2 -a notepad
```

### scroll-into-view
Scroll an element into the visible area.
```bash
winapp ui scroll-into-view e15 -a myapp
```

### wait-for
Wait for an element to appear, disappear, or change.
```bash
winapp ui wait-for Button -a myapp --timeout 5000           # wait for any button
winapp ui wait-for "#Submit" -a myapp --timeout 5000        # wait for specific element
winapp ui wait-for e5 --gone -a myapp --timeout 2000        # wait for element to disappear
```

### list-windows
List all visible windows for an app, including popups and dialogs.
```bash
winapp ui list-windows -a imageresizer
winapp ui list-windows -a Terminal
winapp ui list-windows                                      # all windows (no filter)
```

## Mode Auto-Detection

When connecting to an app, the mode is detected automatically:

1. Named pipe `winapp-winui-{pid}` exists → **DevTools mode**
2. `WinApp.WinUI.dll` loaded but no pipe → Error with guidance
3. Neither → **UIA mode**

Use `--mode uia` to force UIA mode even when DevTools is detected.

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
| "Element e5 is stale" | UI changed since last inspect | Re-run `inspect` or `search` |
| "does not support pattern" | Element can't be invoked/scrolled | Try a different element or action |
| "Pipe not ready" | DevTools NuGet but UseWinAppTools() not called | Add `window.UseWinAppTools()` to startup |
| "No UIA window found" | UIA can't see the process | Use `list-windows` to find the HWND, then `-w` |
| "Window has zero size" | Window is minimized | App will be auto-restored |
