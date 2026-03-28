# C++ WinUI 3 Sample Application

This sample demonstrates a WinUI 3 desktop window built entirely in C++ with CMake - no XAML files, no MIDL, no MSBuild. The UI is constructed programmatically using C++/WinRT projections.

For the console-only version, see the [C++ Sample](../cpp-app/).

## What This Sample Shows

- **Programmatic WinUI 3** — Window, Button, TextBlock, StackPanel created in code
- Using Windows App Model APIs to retrieve package identity
- Using `winapp run` to run the app packaged with MSIX identity
- MSIX packaging with app manifest and assets
- CMake integration with Windows App SDK headers and runtime DLLs

## Prerequisites

- Visual Studio with C++ Desktop development workload
- CMake 3.20 or later
- Windows App SDK runtime installed
- WinApp CLI installed

## Building and Running

### Build the Application

```powershell
cmake -B build
cmake --build build --config Debug
```

### Run without Identity

```powershell
.\build\Debug\cpp-app-winui.exe
```

Click the **Check Identity** button — it will show "Not packaged".

### Run with Identity (Debug)

```powershell
winapp run .\build\Debug
```

This registers a loose layout package (just like a real MSIX install), then launches the WinUI 3 window. Click the **Check Identity** button to see the Package Family Name.

## Technical Notes

This sample uses a non-standard approach to WinUI 3: all UI is built programmatically in C++ without XAML files. This avoids the need for XAML compilation (which requires MSBuild), but means:

- Controls use basic styling (no `XamlControlsResources`)
- The `App` class implements a minimal `IXamlMetadataProvider` (returns null for all types)
- `MddBootstrapInitialize2` handles Windows App SDK initialization for both packaged and unpackaged execution
