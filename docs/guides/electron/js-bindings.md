<!-- mslearn: true -->
# Calling WinRT APIs from JavaScript (JS / TypeScript bindings)

This guide shows you how to call modern Windows Runtime (WinRT) APIs directly from your Electron app's JavaScript or TypeScript — **without** writing a C++ or C# native addon. `winapp` integrates the [`@microsoft/dynwinrt-codegen`](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen) codegen, which produces typed JS + `.d.ts` bindings for WinAppSDK (and any other WinRT) APIs from their `.winmd` metadata. The generated bindings then use [`@microsoft/dynwinrt`](https://www.npmjs.com/package/@microsoft/dynwinrt) to access the underlying WinRT APIs directly at runtime. The result: full IntelliSense at compile time, no `node-gyp` / MSBuild step from your Electron project.

> **When to choose JS bindings over a native addon:** when the API ships in a `.winmd` (most of `Windows.*` and `Microsoft.WindowsAppSDK.*`). Reach for a native addon only when there's no WinRT projection — Win32 / pure COM (raw `IFileDialog`, registry, custom COM servers), C++ libraries that ship only headers + a static/shared lib, or vendor SDKs that ship only a managed .NET assembly. See the C++ / C# addon guides for those cases.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md)
- Used `winapp` via `npx` (the `@microsoft/winappcli` npm package) — JS bindings only work through the npm shim; the standalone winget / installer build doesn't surface them.

## Step 1: Add JS bindings to your project

You have two paths depending on whether your Electron app already has a `winapp.yaml`.

### Path A — Fresh project (init with bindings)

Run `npx winapp init` and opt in to JS bindings (the interactive prompt defaults to Yes; pass `--use-defaults` to auto-accept in scripted / CI runs). `init` installs the WinAppSDK packages, adds a default `"winapp": { "jsBindings": {} }` namespace to `package.json` (covering the full Windows App SDK), and runs the codegen.

```bash
npx winapp init --use-defaults
npm install                        # materializes the @microsoft/dynwinrt runtime dep
```

### Path B — Existing project (layer bindings on)

If `winapp.yaml` already exists and you want to add JS bindings, run `generate-bindings`. The first invocation adds a default `winapp.jsBindings` namespace to `package.json` (covering the full Windows App SDK) and then generates immediately from the winmd lockfile written by your last `winapp restore`:

```bash
npx winapp node generate-bindings
npm install                        # materializes the @microsoft/dynwinrt runtime dep
```

If you want to customize the scope before the first generation, you can still edit `package.json` directly — the empty form covers the full Windows App SDK:

```jsonc
// package.json
{
  "winapp": {
    "jsBindings": {}
  }
}
```

…and then run `npx winapp node generate-bindings` (or `npx winapp restore` if you also need to refresh NuGet packages / the winmd lockfile).

### What you get

Both paths produce a `bindings/` directory next to your sources:

```
bindings/
├── index.js                  # entry — re-exports every emitted class
├── index.d.ts                # TS bundle
├── FileOpenPicker.js         # one pair of files per emitted class
├── FileOpenPicker.d.ts
├── PickerLocationId.js
├── PickerLocationId.d.ts
└── …
```

To put them somewhere else, set `output` inside `winapp.jsBindings` in `package.json` (e.g. `"output": "src/generated/winrt"`) and re-run `restore`.

> [!NOTE]
> If you need to slice generation by NuGet package, add your own `.winmd`, or cherry-pick a few classes from a giant vendor SDK, see [Common workflows](#common-workflows) and the [`package.json` schema](#packagejson--winappjsbindings-namespace) below. This walkthrough sticks to the simplest default-scope flow.

## Step 2: Call a WinRT API from your Electron code

Import from the generated `index.js` — you don't need to know which file inside `bindings/` a class lives in. Here's a native file picker (`Microsoft.Windows.Storage.Pickers.FileOpenPicker`) opened from your Electron main process. This API works on any Windows 11 machine once you've wired up debug identity in [Step 3](#step-3-run-it):

```js
// src/index.js (Electron main)
const { app, BrowserWindow } = require('electron');
const {
  FileOpenPicker,
  PickerLocationId,
  PickerViewMode,
} = require('../bindings/index.js');

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

// Usage (after `app.whenReady()`):
//   const path = await pickAnImage(BrowserWindow.getFocusedWindow());
//   if (path) console.log('Picked:', path);
```

The same `bindings/index.js` re-exports every other emitted class — `AppNotificationManager`, `PowerManager`, `WidgetManager`, and so on. Import what you need; the codegen has already generated typed declarations for everything in your `winapp.jsBindings` scope.

A few conventions to remember:

- **Names are camelCase**, with a trailing underscore when they collide with JS keywords. WinRT `ViewMode` → `viewMode`; reserved words like `default`, `arguments`, `delete` are renamed `default_`, `arguments_`, `delete_`.
- **Construct via static factories, not `new`.** Use `FileOpenPicker.createInstance(windowId)`; WinRT constructor overloads are disambiguated with suffixed names like `createInstance(content)` / `createDefault()`.
- **`UInt64` / `Int64` struct fields and method parameters are typed `bigint`, not `number`.** Use `buffer.readBigUInt64LE(0)` to widen raw OS handles, and build struct values literally — `{ value: hwnd }` — when the WinRT side expects a `WindowId`-style wrapper.
- **Async methods return a `Promise`; pass an `AbortSignal` as the last argument for cancellation:** `await picker.pickSingleFileAsync(signal)`. Operations exposing progress return `WinRTAsyncWithProgress<T, P>` — both `await`-able and exposing `op.progress(cb)` for streaming updates (long downloads, AI token streams).
- **Collections (`IVector_*`, `IMap_*`, `IVectorView_*`) come with JS-friendly helpers** alongside the raw WinRT API: `picker.fileTypeFilter.replaceAll(['*'])`, `vec.toArray()`, `for (const x of vec) …`, `vec.size`.

Events follow an `on<Name>(handler)` shape that returns an unsubscribe function (`const off = obj.onSomething(cb); /* … */ off()`), and `IDisposable` WinRT objects should be wrapped in `try/finally` with a `.close()` call.

You can call the same API from the renderer via `contextBridge` / `ipcRenderer` — exactly as you would for a native addon. The bindings have no dependency on Electron's main process; they work anywhere Node.js can `require()` them.

## Step 3: Run it

WinRT APIs that require an MSIX package identity (notifications, file pickers, …) need debug identity in development. See [Step 5 of the Electron setup guide](setup.md#step-5-understanding-debug-identity) for the full explanation; if you haven't already wired it up, the one-shot command is:

```bash
npx winapp node add-electron-debug-identity
```

> [!NOTE]
> This is already part of the `postinstall` script added during setup, so it usually runs automatically on `npm install`. Re-run it manually whenever you change `Package.appxmanifest`, refresh app assets, or do a clean install.

Now start the app:

```bash
npm start
```

The first call to a WinRT method imported from `bindings/` will load `@microsoft/dynwinrt` and dispatch into the underlying WinRT API — transparent to your code.

## Step 4 (optional): Regenerate after a metadata change

The generated `bindings/` files are build artifacts — gitignore them, or commit for diff visibility, your call. Re-run codegen whenever you change `winapp.yaml` (`packages`, `sdkVersion`, …) or `winapp.jsBindings` in `package.json`:

```bash
# Full restore — refreshes the lockfile (NuGet + cppwinrt headers) and re-runs codegen.
# Use whenever you change `winapp.yaml`.
npx winapp restore

# Fast path — only re-runs dynwinrt-codegen against the cached lockfile.
# Use after editing only `winapp.jsBindings`.
npx winapp node generate-bindings
```

## `package.json` — `winapp.jsBindings` namespace

> **Configuration lives in `package.json`, not `winapp.yaml`.** `winapp.yaml` is owned by the native CLI and only describes SDK package pins; the JS bindings schema lives under `"winapp": { "jsBindings": {...} }` in `package.json` — the same convention used by `eslint`, `jest`, `prettier`, `tsup`, etc. The native CLI has zero awareness of JS bindings.

Full schema with every field shown explicitly:

```jsonc
// package.json
{
  "name": "my-electron-app",
  "version": "0.1.0",
  "winapp": {
    "jsBindings": {
      // Output directory for generated .js + .d.ts (relative to workspace root).
      "output": "bindings",

      // Extra .winmd files to feed into the codegen alongside the ones
      // discovered from `winapp.yaml`'s NuGet packages. Two modes per entry:
      //   * winmdPath only           → bulk-emit the whole winmd
      //   * + namespace + classes    → cherry-pick: only emit the listed
      //                                classes from that namespace (the winmd
      //                                is loaded as ref-only so codegen can
      //                                still resolve its other types).
      // Paths: relative to workspace root, OR absolute. Missing files = warning.
      "additionalWinmds": [
        { "winmdPath": "vendor/MyCompany.Foo.winmd" },
        {
          "winmdPath": "vendor/BigVendor.SDK.winmd",
          "namespace": "BigVendor.Camera",
          "classes": ["Lens", "Sensor"]
        }
      ],

      // Extra .winmd files loaded for type resolution only (no emit).
      // Use for shared dependency winmds your `additionalWinmds` entries
      // reference but you don't want bindings for.
      "additionalRefs": [
        "vendor/BigVendor.Common.winmd"
      ]
    }
  }
}
```

### Field defaults at a glance

| Field | Default | Type |
|-------|---------|------|
| `output` | `"bindings"` | string |
| `additionalWinmds` | `[]` | array of `{winmdPath, namespace?, classes?[]}` |
| `additionalRefs` | `[]` | array of paths |

### Composition rules

1. **NuGet packages** — every package installed via `winapp.yaml` is partitioned by the built-in policy (WinUI / WebView2 = skip; InteractiveExperiences = ref-only; everything else = bulk-emit). The policy isn't user-configurable; install fewer packages in `winapp.yaml` if you want fewer bindings.
2. **`additionalWinmds`** — each entry is either bulk-emitted (no `namespace`/`classes`) or cherry-picked (with both). Cherry-pick entries load the winmd as ref-only and only emit the listed classes.
3. **`additionalRefs`** — appended to the codegen `--ref` channel for type resolution; never emit.
4. **Codegen auto-classification** — `Windows.*` system winmds (and other foundation namespaces) are always loaded as resolution-only refs even when listed under `additionalWinmds` with no `namespace`/`classes`. Use the cherry-pick form (with `namespace` + `classes`) to pull individual classes out of them.

## Common workflows

### Generate bindings for the full WinAppSDK surface

```jsonc
// package.json
{
  "winapp": {
    "jsBindings": {}
  }
}
```

The empty block accepts the defaults: `output: "bindings"`, and every package installed via `winapp.yaml` is bulk-emitted.

> XAML namespaces (`Microsoft.UI.Xaml.*`, `Windows.UI.Xaml.*`) are out of scope for dynwinrt — the codegen itself classifies them automatically as resolution-only refs, so no JS gets emitted for them regardless of which packages are installed.

### Add your own / a vendor `.winmd`

```jsonc
{
  "winapp": {
    "jsBindings": {
      "additionalWinmds": [
        { "winmdPath": "vendor/MyCompany.Foo.winmd" },   // relative to workspace root
        { "winmdPath": "C:/shared/OtherSdk.winmd" }       // absolute also works
      ]
    }
  }
}
```

The winmd is bulk-emitted: every public class inside gets a JS + `.d.ts` pair.

### Cherry-pick a few classes from a giant vendor SDK

```jsonc
{
  "winapp": {
    "jsBindings": {
      "additionalWinmds": [
        {
          "winmdPath": "vendor/BigVendor.SDK.winmd",
          "namespace": "BigVendor.Camera",
          "classes": ["Lens", "Sensor"]
        }
      ]
    }
  }
}
```

The winmd is loaded for type resolution, but only `BigVendor.Camera.Lens` and `BigVendor.Camera.Sensor` get JS bindings emitted. The same pattern works for cherry-picking from system `Windows.*` winmds.

### Override the output directory

```jsonc
{
  "winapp": {
    "jsBindings": {
      "output": "src/generated/winrt"
    }
  }
}
```

## Runtime dependency injection

When `init` (or `restore`) runs the JS-bindings step on a workspace, the CLI:

1. Detects your project's package manager from the `packageManager` field in `package.json`, then falls back to lockfile sniffing (`pnpm-lock.yaml` → pnpm, `yarn.lock` → yarn, `bun.lockb` → bun, `package-lock.json` → npm). Defaults to **npm** if no signal exists.
2. Adds `@microsoft/dynwinrt` to your `package.json` `dependencies` (production dep, NOT devDep) — your generated bindings `import` from it at module load, so it must ship in your installed app.
3. Prints a PM-aware install hint (`npm install` / `pnpm install` / `yarn install` / `bun install`) so you know what to run next.

Supported package managers: **npm, pnpm, yarn, bun**.

> Why production not devDep? `@microsoft/dynwinrt` is the runtime that powers the generated bindings — without it, your generated `bindings/*.js` files fail to load at runtime. It's not a build-only tool.

## How it works under the hood

```
   ┌─────────────────────┐     ┌─────────────────────────────┐
   │  winapp.yaml        │     │  package.json               │
   │  (native CLI owns)  │     │  "winapp": { "jsBindings" } │
   │  packages: ...      │     │  (npm wrapper owns)         │
   └──────────┬──────────┘     └──────────────┬──────────────┘
              │                                │
              │ (winapp restore)               │ (npm wrapper post-restore)
              ▼                                ▼
   ┌──────────────────────────────────────────┐
   │  WorkspaceSetupService (native)          │
   │  • restore NuGet packages                │
   │  • discover .winmd files                 │
   │  • write .winapp/winmds.lock.json        │
   │  • generate cppwinrt projections         │
   └──────────────────────────────────────────┘
              │
              ▼ (npm wrapper sees winapp.jsBindings in package.json)
   ┌──────────────────────────────────────────┐
   │  JS bindings orchestrator (npm wrapper)  │
   │  • partition winmds: emit / ref-only /   │
   │    skip (per built-in winmd-policy)      │
   │  • resolve additionalWinmds /            │
   │    additionalRefs paths                  │
   │  • safety-check output dir               │
   │    (.dynwinrt-managed marker)            │
   │  • spawn @microsoft/dynwinrt-codegen     │
   │    --winmd "p1;p2;..." --ref "r1;..."    │
   │  • write .dynwinrt-managed marker        │
   └──────────┬───────────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────────┐
   │  @microsoft/dynwinrt-codegen             │
   │  • loads emit winmds + ref winmds        │
   │  • auto-classifies Windows.* as          │
   │    resolution-only refs                  │
   │  • generates .js + .d.ts                 │ → bindings/<Namespace>.<Class>.{js,d.ts}
   └──────────────────────────────────────────┘
              │ (at app runtime)
              ▼
   ┌──────────────────────────────────────────┐
   │  @microsoft/dynwinrt                     │ (production dep injected into your package.json)
   │  • dynamic WinRT invocation              │
   │  • COM marshaling, async, delegates      │
   └──────────────────────────────────────────┘
```

### Per-package winmd categorization

Some WinAppSDK packages ship `.winmd` files that dynwinrt cannot drive at runtime (XAML composables, UI Composition primitives). To keep the generated tree usable, winapp applies a **package-level policy** before handing winmds to the codegen:

| Package | Category | Why |
|---------|----------|-----|
| `Microsoft.WindowsAppSDK.WinUI` | **Skip** | Pure XAML composables — `Button`, `Page`, `Application` etc. dynwinrt has no way to host. |
| `Microsoft.Web.WebView2` | **Skip** | Pulled in transitively by WinAppSDK (for the XAML `<WebView2>` control). The whole surface is HWND / Composition-hosted browser embedding — useless from a headless Node / Electron JS context (Electron already renders via Chromium). |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | **Ref-only** | Ships `Microsoft.UI.WindowId`, `Microsoft.Graphics.PointInt32`, `Microsoft.UI.Color` and other primitive types widely referenced by Foundation/Storage/Notifications APIs — must stay loaded for type resolution, but its own runtime classes are XAML/Composition types winapp cannot drive. |
| Everything else | **Emit** | Bulk-generate JS bindings (codegen still auto-classifies `Windows.*` as refs internally). |

This split happens in the npm wrapper's `winmd-policy.ts` (`partitionByPackageCategory`). Skipped winmds aren't passed to the codegen at all; ref-only winmds flow through the codegen `--ref` channel.

**Escape hatch**: if you need the contents of a Skip/Ref-only package (vendor fork, experimentation), list its winmd files explicitly under `winapp.jsBindings.additionalWinmds` — those flow through the user-additional channel and bypass the policy above.

### The `.dynwinrt-managed` marker and `winmds.lock.json`

After a successful generation winapp writes `<output>/.dynwinrt-managed` into the output directory. Its presence is the **only** signal winapp uses to know that a non-empty output directory is safe to wipe before the next codegen run. **Never delete this file by hand**, and never put files you care about under the codegen output directory — the next `restore` will wipe anything other than the marker. (If the directory is non-empty and the marker is missing, winapp aborts rather than risk overwriting hand-written code.)

In addition, `winapp restore` writes `.winapp/winmds.lock.json` — a human-readable snapshot of every NuGet package the restore resolved, with its version and the per-package winmd discovery results. The lockfile is the bridge between the native `winapp restore` (which writes it) and the npm wrapper (which reads it and applies the emit/refOnly/skip policy at codegen time). It's also a useful diagnostic artifact:

- Useful to share when reporting bugs: it tells the maintainer exactly what got resolved without us needing to repro your NuGet feed setup.
- Records a SHA-256 of the top-level `packages:` block so you can spot yaml drift between restore runs.

**Write atomicity**: lockfile writes go through a per-call `.tmp.<guid>` sibling that's renamed into place on completion, so concurrent readers always see a consistent (old or new) file, never a torn write.

## Next steps

- **CLI** — [`npx winapp init` reference](../../usage.md#init) and [`npx winapp restore` reference](../../usage.md#restore).
- **Runtime** — [`@microsoft/dynwinrt` on GitHub](https://github.com/microsoft/dynwinrt) — the runtime that powers the generated bindings.
- **Codegen** — [`@microsoft/dynwinrt-codegen` on GitHub](https://github.com/microsoft/dynwinrt) — the code-generation tool (same repo as `dynwinrt`).
- **Package & ship** — [Packaging Your App](packaging.md) once you're ready to produce an MSIX for distribution.
