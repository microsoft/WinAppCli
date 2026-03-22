---
name: winapp-ui-automation
description: Inspect and interact with running Windows app UIs from the command line using UI Automation (UIA). Use when an AI agent or developer needs to inspect a UI element tree, find controls, take screenshots, click buttons, read or set text, or verify UI state in a running Windows app. Works with any framework: WinUI 3, WPF, WinForms, Win32, Electron.
version: 0.2.1
---
## When to use
- Inspecting a running Windows app's UI from the command line
- AI agents interacting with Windows applications (clicking buttons, reading text, taking screenshots)
- Verifying UI state during development or testing
- Automating UI workflows without Playwright or Selenium
- Debugging WinUI 3, WPF, WinForms, Win32, or Electron app UIs

## Prerequisites
- For UIA mode (any app): No setup needed — works with any running Windows app

## Key concepts
- **Selectors**: Target elements by ID (`e5`), name (`#Submit`), AutomationId (`$SearchBox`), or type (`Button`)
- **`-a` vs `-w`**: Use `-a` to find apps by name/title/PID. Use `-w <HWND>` for stable window targeting (survives tab switches)
- **Element IDs**: Assigned by `inspect`/`search`. Valid until the next `inspect`/`search`. Use selectors (`#Name`, `$AutomationId`) for stable references

## Usage

### Discover and connect
```powershell
# Find an app
winapp ui status -a notepad

# List windows when ambiguous
winapp ui list-windows -a Terminal
# → HWND 985238: "Tab 1" (WindowsTerminal, PID 21228)
# → HWND 131906: "Tab 2" (WindowsTerminal, PID 21228)
```

### Inspect element tree
```powershell
winapp ui inspect -a notepad
winapp ui inspect -w 131906 --depth 5
```

### Find elements
```powershell
winapp ui search Button -a notepad
winapp ui search "#Close" -a notepad
winapp ui search ComboBox -a imageresizer
```

### Interact
```powershell
# Click a button
winapp ui invoke "#Submit" -a myapp

# Type text
winapp ui set-value e2 --text "Hello" -a notepad

# Expand a combo box
winapp ui invoke '$SizeComboBox' -a imageresizer

# Focus an element
winapp ui focus e5 -a myapp
```

### Screenshot
```powershell
# Save to file
winapp ui screenshot -a myapp

# JSON response with file path (for agents)
winapp ui screenshot -a myapp --json

# Screenshot a popup (use list-windows to find HWND)
winapp ui list-windows -a myapp
winapp ui screenshot -w <popup-hwnd>
```

### Wait for UI state
```powershell
winapp ui wait-for "#Submit" -a myapp --timeout 5000
winapp ui wait-for e5 --gone -a myapp --timeout 2000
```

## Tips
- Use `list-windows` to discover popup windows, dialogs, and dropdown overlays
- When `-a` shows "multiple windows", switch to `-w <HWND>` for stability
- Element IDs reset on each `inspect`/`search` — use them immediately
- For elements with duplicate names, element IDs disambiguate by position
- Use `--json` for machine-readable output, omit for human-readable tables/lists
- Use `--mode uia` to force UIA mode when DevTools detection causes issues

## Related skills
- `winapp-setup` for adding Windows SDK and DevTools to your project
- `winapp-package` for packaging apps as MSIX

## Troubleshooting
| Error | Cause | Solution |
|---|---|---|
| "No running app found" | Wrong name or app not running | Try process name, window title, or PID |
| "Multiple windows match" | Several windows match `-a` | Use `-w <HWND>` from the listed options |
| "Element is stale" | UI changed since inspect/search | Re-run `inspect` or `search` |
| "does not support pattern" | Element can't be invoked | Try `search` to find the right control type |
| "No UIA window found" | UIA can't see the window | Use `list-windows` to find HWND, then `-w` |


## Command Reference

### `winapp ui status`

Connect to a target app, auto-detect mode (UIA or DevTools), and display connection info.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui inspect`

View the UI element tree. Shows ControlType, Name, AutomationId, and bounds for each element.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--ancestors` | Walk up the tree from the specified element to the root | (none) |
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--depth` | Tree inspection depth | `3` |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui search`

Search the element tree for elements matching a selector. Returns all matches with IDs.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--max` | Maximum search results | `50` |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui get-property`

Read UIA property values from an element. Specify --property for a single property or omit for all.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--property` | Property name to read or filter on | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui screenshot`

Capture the target window or a specific element as a PNG image. With --json, returns base64-encoded PNG inline. With --output, saves to file.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--output` | Save output to file path (e.g., screenshot) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui invoke`

Programmatically activate an element. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui set-value`

Set text on an element using UIA ValuePattern. Works for TextBox, ComboBox, and other editable controls.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--text` | Text value to set or type | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui focus`

Move keyboard focus to the specified element using UIA SetFocus.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui scroll-into-view`

Scroll the specified element into the visible area using UIA ScrollItemPattern.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui wait-for`

Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<selector>` | No | Element selector: e5 (ID), #Name, $AutomationId, Type, or Type#Name |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--gone` | Wait for element to disappear instead of appear | (none) |
| `--json` | Format output as JSON | (none) |
| `--mode` | Force connection mode: 'uia' (skip DevTools detection) or 'auto' (default) | (none) |
| `--property` | Property name to read or filter on | (none) |
| `--timeout` | Timeout in milliseconds | `5000` |
| `--value` | Wait for property to equal this value (use with --property) | (none) |
| `--window` | Target window by HWND (stable handle from list output). Takes precedence over --app. | (none) |

### `winapp ui list-windows`

List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--app` | Target app (process name, window title, or PID). Lists windows if ambiguous. | (none) |
| `--json` | Format output as JSON | (none) |
