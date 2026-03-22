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
