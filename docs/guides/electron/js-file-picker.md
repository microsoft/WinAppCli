<!-- mslearn: true -->
# Call a Windows File Picker from JavaScript (JS bindings)

This guide shows you how to call a Windows Runtime (WinRT) API — the native Windows file picker — directly from your Electron app's JavaScript, with no native addon and no `node-gyp` / MSBuild step. It's a great starting point for calling any `Windows.*` or `Microsoft.WindowsAppSDK.*` API from JS/TS with full IntelliSense.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).

## Step 1: Confirm your bindings

Setup generated a `.winapp/bindings/` directory next to your sources — one `.js` + `.d.ts` pair per WinRT class, plus an `index.js` that re-exports them all:

```
.winapp/bindings/
├── index.js                  # entry — re-exports every emitted class
├── index.d.ts                # TS bundle
├── FileOpenPicker.js         # one pair of files per emitted class
├── FileOpenPicker.d.ts
├── PickerLocationId.js
├── PickerLocationId.d.ts
└── …
```

## Step 2: Call a WinRT API from your Electron code

Load classes from the generated `index.js` — you don't need to know which file inside `.winapp/bindings/` a class lives in. Here's a native file picker (`Microsoft.Windows.Storage.Pickers.FileOpenPicker`) opened from your Electron main process. This API works on any Windows 11 machine once you've wired up debug identity in [Step 3](#step-3-run-it):

```js
// src/index.js (Electron main, CommonJS)
const { app, BrowserWindow, ipcMain } = require('electron');
const {
  FileOpenPicker,
  PickerLocationId,
  PickerViewMode,
} = require('../.winapp/bindings/index.js');

async function pickAnImage(mainWindow) {
  // FileOpenPicker needs the parent window's HWND wrapped in a WindowId struct.
  // Electron's getNativeWindowHandle() returns an 8-byte buffer on 64-bit Windows.
  const hwnd = mainWindow.getNativeWindowHandle().readBigUInt64LE(0);

  const picker = FileOpenPicker.createInstance({ value: hwnd });
  picker.viewMode = PickerViewMode.Thumbnail;
  picker.suggestedStartLocation = PickerLocationId.PicturesLibrary;
  picker.fileTypeFilter.replaceAll(['.png', '.jpg', '.jpeg', '.gif']);

  const result = await picker.pickSingleFileAsync();
  return result?.path; // string with the chosen path, or undefined if the user cancelled
}

// Expose it to the renderer via IPC so a button click can trigger the picker.
ipcMain.handle('pick-image', (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  return pickAnImage(win);
});
```

Then bridge it into the renderer through your preload script:

```js
// src/preload.js
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('winapp', {
  pickImage: () => ipcRenderer.invoke('pick-image'),
});
```

Finally, add a button to your renderer and call `window.winapp.pickImage()` when it's clicked:

```html
<!-- src/index.html -->
<button id="pick">Pick an image</button>
<p id="result"></p>

<script>
  document.getElementById('pick').addEventListener('click', async () => {
    const filePath = await window.winapp.pickImage();
    document.getElementById('result').textContent = filePath ?? 'Cancelled';
  });
</script>
```

> [!NOTE]
> These examples are CommonJS (`require`). In an ESM project (`"type": "module"` or TypeScript), use a top-level `import` instead:
> ```js
> import { FileOpenPicker, PickerLocationId, PickerViewMode } from '../.winapp/bindings/index.js';
> ```

> [!IMPORTANT]
> Using **Vite**? Externalize `@microsoft/dynwinrt` in `vite.main.config.mjs`:
> ```js
> import { defineConfig } from 'vite';
>
> export default defineConfig({
>   build: {
>     rollupOptions: {
>       external: ['@microsoft/dynwinrt'],
>     },
>   },
> });
> ```

A few conventions the example shows:

- **Members are camelCase** (`ViewMode` → `viewMode`); names colliding with JS keywords get a trailing underscore (`default_`, `delete_`).
- **Construct via static factories, not `new`** — `FileOpenPicker.createInstance(windowId)`.
- **`UInt64` / `Int64` are `bigint`** — use `buffer.readBigUInt64LE(0)` for raw handles, and pass struct wrappers literally (`{ value: hwnd }`).
- **Async methods return a `Promise`**, and collections expose helpers like `replaceAll(...)`, `toArray()`, `for…of`, and `.size`.

## Step 3: Run it

Before the file picker will work, you need to ensure your app runs with identity. Run:

```bash
npx winapp node add-electron-debug-identity
```

> [!NOTE]
> This command is already part of the `postinstall` script we added in the setup guide, so it runs automatically after `npm install`. However, you need to run it manually whenever you modify `Package.appxmanifest`, update app assets, or reinstall dependencies.

Now start the app:

```bash
npm start
```

Click the button and the native Windows file picker appears! 🎉 Importing from `.winapp/bindings/` loads `@microsoft/dynwinrt`, which dispatches each call into the underlying WinRT API — transparent to your code.

## Step 4 (optional): Regenerate after a metadata change

The generated `.winapp/bindings/` files are build artifacts — `.winapp/` is added to `.gitignore` by `init`, so you regenerate them rather than committing. Re-run codegen whenever you change `winapp.yaml` (`packages`, `sdkVersion`, …) or `winapp.jsBindings` in `package.json`:

```bash
# Full restore — refreshes the lockfile (NuGet + cppwinrt headers) and re-runs codegen.
# Use whenever you change `winapp.yaml`.
npx winapp restore

# Fast path — only re-runs dynwinrt-codegen against the cached lockfile.
# Use after editing only `winapp.jsBindings`.
npx winapp node generate-bindings
```

> [!WARNING]
> Treat the output directory (`.winapp/bindings/`) as fully managed by `winapp` — never put hand-written files there. Each regeneration wipes the directory, keeping only the `.dynwinrt-managed` marker `winapp` uses to recognize it as safe to overwrite.

## Customizing the binding scope (optional)

By default — the empty `"jsBindings": {}` block that `init` adds — `winapp` generates bindings for the WinAppSDK packages in your `winapp.yaml`, minus a few that can't be driven from a headless Node process (XAML/WinUI and WebView2 are excluded by default). To narrow or extend that, configure the `winapp.jsBindings` namespace in `package.json` (the schema lives in `package.json`, not `winapp.yaml`, the same convention used by `eslint`, `jest`, `prettier`, …):

```jsonc
// package.json
{
  "winapp": {
    "jsBindings": {
      // Extra .winmd files to generate bindings from. Each entry is one of:
      //   { "winmdPath": "..." }                                       emit the whole winmd
      //   { "winmdPath": "...", "namespace": "...", "classes": [...] }  cherry-pick from it
      //   { "namespace": "Windows.Storage", "classes": [...] }         cherry-pick from the Windows SDK
      // Paths are relative to the workspace root, or absolute.
      "additionalWinmds": [
        { "winmdPath": "vendor/MyCompany.Foo.winmd" },
        { "namespace": "Windows.Storage", "classes": ["StorageFile"] }
      ],

      // Extra .winmd files loaded for type resolution only — never emitted.
      "additionalRefs": ["vendor/BigVendor.Common.winmd"]
    }
  }
}
```

Re-run `npx winapp node generate-bindings` after editing the block. XAML namespaces (`Microsoft.UI.Xaml.*`, `Windows.UI.Xaml.*`) are always out of scope — the codegen can't host them, so no JS is emitted regardless of which packages are installed.

## Next Steps

Congratulations! You're now calling WinRT APIs directly from JavaScript — no native addon, no `node-gyp` build step. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship (the `@microsoft/dynwinrt` runtime is already in your `dependencies`).

Or explore other guides:
- **[Creating a C++ Native Addon](cpp-notification-addon.md)** — for Win32 / pure-COM APIs that have no WinRT projection.
- **[Creating a Phi Silica Addon](phi-silica-addon.md)** — Windows AI APIs from a C# addon.
- **[Getting Started Overview](index.md)** — return to the main guide.

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** — full CLI reference (`init`, `restore`, `node generate-bindings`).
- **[Sample Electron App](../../../samples/electron/)** — complete working example, including JS bindings.
- **[@microsoft/dynwinrt](https://github.com/microsoft/dynwinrt)** — the runtime that powers the generated bindings.
- **[@microsoft/dynwinrt-codegen](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator.
