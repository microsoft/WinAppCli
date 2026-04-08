# WinUI 3 Sample Application

This sample demonstrates a WinUI 3 desktop application with UI controls designed for testing `winapp ui` commands.

## What This Sample Shows

- WinUI 3 desktop app with Mica backdrop and custom TitleBar
- Interactive controls with `AutomationProperties` for UI automation
- Button with counter, TextBox, CheckBox, and submit flow
- Used as the target app for `winapp ui` end-to-end tests

## Controls & AutomationIds

| Control | AutomationId | Purpose |
|---------|-------------|---------|
| Button "Click Me" | `CounterButton` | Increments counter on click |
| TextBlock "Count: N" | `CounterDisplay` | Shows click count |
| TextBox | `TextInput` | Accepts text input |
| CheckBox "Enable feature" | `FeatureToggle` | Toggle on/off |
| Button "Submit" | `SubmitButton` | Submits form |
| TextBlock (result) | `ResultDisplay` | Shows submission result |

## Building and Running

```powershell
# Build
dotnet build -c Debug -p:Platform=x64

# Launch with winapp
winapp run .\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64

# Or launch detached for automation
winapp run .\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach --json
```

## Testing with winapp ui

```powershell
# Inspect the UI tree
winapp ui inspect -a winui-app

# Click the counter button
winapp ui invoke "Counter Button" -a winui-app

# Verify counter updated
winapp ui wait-for "Counter Display" -a winui-app --property Name --value "Count: 1" -t 5000

# Enter text and submit
winapp ui set-value "Text Input" "Hello world" -a winui-app
winapp ui invoke "Feature Toggle" -a winui-app
winapp ui invoke "Submit Button" -a winui-app

# Verify result
winapp ui wait-for "Result Display" -a winui-app --property Name --value "Submitted: Hello world (Feature: On)" -t 5000

# Take a screenshot
winapp ui screenshot -a winui-app -o result.png
```

## E2E Test Script

The full automated test is in `scripts/test-e2e-winui-ui.ps1`. It exercises all `winapp ui` commands against this app.
