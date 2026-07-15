# Node.js WinUI 3 Sample

This sample creates a WinUI 3 `Application` and `Window` directly from Node.js.
The `Microsoft.UI.Xaml` controls are projected into JavaScript by dynwinrt. It
does not use Electron, HTML, a WebView, XAML markup, XAML Islands, or a native
addon.

The window contains a Fluent card layout implemented with `Grid`, `Border`,
`StackPanel`, `TextBlock`, `ComboBox`, and `Button`. Each button invokes a
JavaScript callback that updates JavaScript state and WinUI properties. The
theme picker switches the root element between the system, light, and dark
themes and applies the same selection to the system title bar.

## Prerequisites

- Windows 11 with Developer Mode enabled
- Node.js 20 or later
- `@microsoft/winappcli` 1.0 or later
- `@microsoft/dynwinrt` and `@microsoft/dynwinrt-codegen` preview.13 or later

## Run the sample

```powershell
npm install
npm run restore
npm start
```

`npm run restore` downloads the SDK metadata and generates JavaScript bindings
under `.winapp\bindings`. The app loads them through the
`#winapp/bindings` package import declared in `package.json`.

`npm start` copies the current `node.exe` into `.local-node`, registers the
folder as a loose-layout package, launches Node with package identity and the
Windows App SDK runtime graph, and unregisters the package when the window
closes.

Do not launch this sample with `node main.js`. WinUI activation requires the
package identity and runtime setup supplied by `winapp run`.

## Architecture

`main.js` creates a Node worker. The worker:

1. Initializes a single-threaded WinRT apartment.
2. Starts the WinUI `Application` dispatcher loop.
3. Composes the application with a WinUI metadata provider and Fluent resources.
4. Creates a `Window` with a Mica backdrop.
5. Creates the controls imperatively from generated JavaScript bindings.
6. Activates the window and exits the application when it closes.

`Application.start()` owns the calling thread until the application exits. The
sample runs it in a worker so the main Node.js event loop remains available.

`Application.createWithFluentResources()` installs the standard WinUI control
templates and theme resources. It also configures Per-Monitor V2 DPI awareness,
so the associated `AppWindow` size is converted from view pixels to physical
pixels after the content loads. The sample uses `AccentButtonStyle` from those
resources and refreshes its card brushes when the active Windows theme changes.

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
