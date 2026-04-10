# C++ WinUI 3 Crash Test App

A C++/WinRT WinUI 3 app that intentionally crashes in different ways, used for testing
`winapp run --debug-output` crash dump capture and native stack analysis.

## Crash Types

| Button | Crash Type | Exception Code |
|--------|-----------|----------------|
| Access Violation | Null pointer write | 0xC0000005 |
| Stack Overflow | Infinite recursion with stack allocation | 0xC00000FD |
| C++ Exception | `std::runtime_error` throw | 0xE06D7363 |
| Timed Crash | Access violation after 3s delay | 0xC0000005 |

## Build

```powershell
winapp restore
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Debug
```

## Run with crash diagnostics

```powershell
winapp run build\Debug --debug-output
winapp run build\Debug --debug-output --symbols   # with symbol download
```
