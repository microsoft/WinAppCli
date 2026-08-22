# Node.js WinUI 3 Sample

This sample creates a WinUI 3 `Application` and `Window` directly from Node.js.
The `Microsoft.UI.Xaml` controls are projected into JavaScript by dynwinrt. It
does not use Electron, HTML, a WebView, XAML markup, XAML Islands, or a native
addon specific to the sample.

The window contains a Fluent card layout implemented with `Grid`, `Border`,
`StackPanel`, `TextBlock`, `ComboBox`, and `Button`. Each button invokes a
JavaScript callback that updates JavaScript state and WinUI properties. The
theme picker switches the root element between the system, light, and dark
themes and applies the same selection to the system title bar.

## Prerequisites

- Windows 11
- Node.js 20 or later
- `@microsoft/winappcli` 1.0 or later
- `@microsoft/dynwinrt` and `@microsoft/dynwinrt-codegen` preview.15 or later

## Run the sample

```powershell
npm install
npm run restore
npm run prepare-runtime
npm start
```

`npm run restore` downloads the SDK metadata and generates JavaScript bindings
under `.winapp\bindings`. The app loads the bindings through the
`#winapp/bindings` package import declared in `package.json`.

`npm run prepare-runtime` uses the typed `runtimePrepare()` Node SDK wrapper to
resolve Windows App SDK 2.2 for the Node process architecture, install the exact
matching framework-dependent runtime for the current user when needed, and
stage the bootstrap DLL under `.winapp\runtime\<arch>`. The JSON result supplies
the resolved version and deterministic bootstrap path; the sample does not
search the NuGet cache or depend on `restore`'s internal output layout.

`npm start` launches `node main.js` directly without package identity. The main
thread loads the prepared bootstrap DLL and initializes the process-wide
Windows App SDK runtime graph before creating the UI worker.

## Architecture

`main.js` bootstraps Windows App SDK 2.2 once for the process, then creates a
Node worker. The worker:

1. Initializes a single-threaded WinRT apartment.
2. Starts the WinUI `Application` dispatcher loop.
3. Composes the application with a WinUI metadata provider and Fluent resources.
4. Creates a `Window` with a Mica backdrop.
5. Creates the controls imperatively from generated JavaScript bindings.
6. Activates the window and exits the application when it closes.

Constructible WinRT classes use normal JavaScript constructors, such as
`new Window()`, `new StackPanel()`, and `new SolidColorBrush(color)`.

`Application.start()` owns the calling thread until the application exits. The
sample runs it in a worker so the main Node.js event loop remains available.
Windows App SDK bootstrap is process-wide, while WinRT apartment initialization
is thread-local, so `roInitialize(0)` remains in the UI worker.

`Application.create()` installs the standard WinUI control templates and theme
resources. In an unpackaged process, dynwinrt resolves the
framework `resources.pri` from the bootstrapped package graph and supplies it to
WinUI's resource manager. The helper also configures Per-Monitor V2 DPI
awareness, so the associated `AppWindow` size is converted from view pixels to
physical pixels after the content loads. The sample uses `AccentButtonStyle`
from those resources and refreshes its card brushes when the active Windows
theme changes.

## Regenerate bindings

After changing `winapp.jsBindings` in `package.json`, run:

```powershell
npm run restore
```

`ScrollViewer` is included as a binding root because its metadata causes
codegen to emit the `IVector_UIElement` projection used to append child
controls.

If only the generated output was removed and `winapp.yaml` has not changed, use:

```powershell
npm run generate
```
