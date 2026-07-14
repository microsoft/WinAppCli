# Node.js WinUI 3 Sample

This sample creates a native WinUI 3 window directly from Node.js. The UI uses
real `Microsoft.UI.Xaml` controls projected into JavaScript by dynwinrt. It does
not use Electron, HTML, a WebView, XAML markup, or a native addon.

The window contains a counter implemented with `StackPanel`, `TextBlock`, and
`Button`. Each button invokes a JavaScript callback that reads and updates WinUI
properties.

## Prerequisites

- Windows 11 with Developer Mode enabled
- Node.js 20 or later
- `@microsoft/winappcli` 1.0 or later
- `@microsoft/dynwinrt` and `@microsoft/dynwinrt-codegen` preview.11 or later

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
2. Creates a `DispatcherQueueController`.
3. Initializes WinUI with `WindowsXamlManager`.
4. Hosts XAML content in an `AppWindow` through `DesktopWindowXamlSource`.
5. Creates the controls imperatively from generated JavaScript bindings.
6. Resizes the XAML site bridge whenever the `AppWindow` client area changes.
7. Runs the WinUI dispatcher loop until the window closes.

The dispatcher loop blocks only the worker thread, so the main Node.js event
loop remains available.

The sample uses an explicit palette because WinUI's compiled `App.xaml` theme
resources are not loaded automatically in this hosting model.

## Regenerate bindings

After changing `winapp.jsBindings` in `package.json`, run:

```powershell
npm run restore
```

If only the generated output was removed and `winapp.yaml` has not changed, use:

```powershell
npm run generate
```
