# WinUI Crash Test App

A WinUI 3 app that intentionally crashes in different ways, used for testing
`winapp run --debug-output` crash dump capture and analysis.

## Crash Types

| Button | Exception | Code |
|--------|-----------|------|
| Access Violation | Null pointer write | 0xC0000005 |
| Stack Overflow | Infinite recursion | 0xC00000FD |
| Managed Exception | NullReferenceException | 0xE0434352 |
| Timed Crash | Access violation after 3s delay | 0xC0000005 |

## Usage

```powershell
# Build the app
dotnet build -c Debug -p:Platform=x64

# Run with crash dump capture
winapp run bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --debug-output
```
