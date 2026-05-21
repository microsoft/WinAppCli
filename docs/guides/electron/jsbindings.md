<!-- mslearn: true -->
# Calling WinRT APIs from JavaScript (JS / TypeScript bindings)

This guide shows you how to call modern Windows Runtime (WinRT) APIs directly from your Electron app's JavaScript or TypeScript — **without** writing a C++ or C# native addon. `winapp` integrates the [`@microsoft/dynwinrt-codegen`](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen) codegen, which produces typed JS + `.d.ts` bindings for WinAppSDK (and any other WinRT) APIs from their `.winmd` metadata. The generated bindings then use [`@microsoft/dynwinrt`](https://www.npmjs.com/package/@microsoft/dynwinrt) to access the underlying WinRT APIs directly at runtime. The result: full IntelliSense at compile time, no `node-gyp` / MSBuild step from your Electron project.

> **When to choose JS bindings over a native addon:** when you only need to *call* WinRT APIs (load a model, run inference, send a notification, read a sensor) and don't need a stateful C++/C# service or APIs `dynwinrt` doesn't yet drive (XAML, DispatcherQueue). For data-style WinRT APIs, JS bindings are the easier on-ramp; for stateful or UI-hosting scenarios, see the C++ / C# addon guides.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md)
- Used `winapp` via `npx` (i.e., the `@microsoft/winappcli` npm package) — JS bindings are gated to npm-invoked `winapp` because the generator (`@microsoft/dynwinrt-codegen`) and runtime (`@microsoft/dynwinrt`) ship as npm dependencies. The standalone winget / installer build does not surface the bindings prompt and does not generate JS bindings.

## Step 1: Add JS bindings to your project

You have two paths depending on whether your Electron app already has a `winapp.yaml`.

### Path A — Fresh project (init with bindings prompt)

When you run `npx winapp init` for the first time, the CLI shows an interactive yes/no prompt asking whether to add JS bindings on top of the standard C++ projection workspace:

```bash
npx winapp init
# > Add JS/TypeScript bindings to this project? [Y/n]:
```

Press **Enter** (default Yes) to opt in. `init` installs the WinAppSDK packages, generates the C++ projections (always), adds a default `"winapp": { "jsBindings": {} }` namespace to `package.json` (covering the full Windows App SDK), and runs the codegen.

For a scripted / CI install, `--use-defaults` auto-opts in without prompting:

```bash
npx winapp init --use-defaults
npm install                        # picks up the @microsoft/dynwinrt runtime dep that init injected
```

### Path B — Existing project (layer bindings on)

If `winapp.yaml` already exists and you want to add JS bindings, run `generate-bindings`. The first invocation adds a default `winapp.jsBindings` namespace to `package.json` (covering the full Windows App SDK) and then generates immediately from the winmd lockfile written by your last `winapp restore`:

```bash
npx winapp node generate-bindings
npm install                        # picks up the @microsoft/dynwinrt runtime dep
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
├── index.js                                          # entry — re-exports every emitted class
├── index.d.ts                                        # TS bundle
├── Microsoft.Windows.Vision.TextRecognizer.js
├── Microsoft.Windows.Vision.TextRecognizer.d.ts
├── Microsoft.Windows.AI.Generative.LanguageModel.js
├── Microsoft.Windows.AI.Generative.LanguageModel.d.ts
└── …                                                 # one pair of files per emitted class
```

To put them somewhere else, set `output` inside `winapp.jsBindings` in `package.json` (e.g. `"output": "src/generated/winrt"`) and re-run `restore`.

> [!NOTE]
> If you need to slice generation by NuGet package, add your own `.winmd`, or cherry-pick a few classes from a giant vendor SDK, see the recipes in [JS / TypeScript bindings for WinRT](../../js-bindings.md). This guide sticks to the simplest default-scope flow.

## Step 2: Call a WinRT API from your Electron code

Import from the generated `index.js` — you don't need to know which file inside `bindings/` a class lives in. Here's an OCR (text recognition) flow as it would run in your Electron main process. We use `TextRecognizer` rather than `LanguageModel` because it doesn't require a Limited Access Feature token, so you can run this end-to-end on any Copilot+ PC without applying for access:

```js
// src/index.js (Electron main)
const path = require('path');
const {
  TextRecognizer,
  AIFeatureReadyState,
} = require('./bindings/index.js');

async function recognizeText(imagePath) {
  // First-run model download (one time per user) — cheap no-op once cached.
  if (TextRecognizer.getReadyState() !== AIFeatureReadyState.ready) {
    await TextRecognizer.ensureReadyAsync();
  }

  const recognizer = await TextRecognizer.createAsync();
  try {
    const recognized = await recognizer.recognizeTextFromImageAsync(imagePath);
    return recognized.lines.map(line => ({
      text: line.text,
      x: line.boundingBox.topLeft.x,
      y: line.boundingBox.topLeft.y,
    }));
  } finally {
    recognizer.close();
  }
}

// Usage:
// const lines = await recognizeText(path.join(__dirname, 'screenshot.png'));
// lines.forEach(l => console.log(`(${l.x}, ${l.y}): ${l.text}`));
```

For the full text-generation (Phi Silica `LanguageModel`) flow — which also lives in the same `bindings/` output — see the [Windows AI APIs reference](https://learn.microsoft.com/windows/ai/apis/). That surface requires a [Limited Access Feature token](https://learn.microsoft.com/windows/apps/develop/limited-access-features) before `LanguageModel.createAsync()` will succeed.

A few conventions to remember:

- **Method names are camelCase.** WinRT methods like `RecognizeTextFromImageAsync` become `recognizeTextFromImageAsync`; properties like `line.Text` become `line.text`. The codegen lowercases the first letter to match JavaScript style.
- **Structs use a `create()` factory, not `new`.** For example, `LanguageModelOptions.create()` — not `new LanguageModelOptions()`.
- **Async methods return a `progressOperation` thenable.** It's both `await`-able and exposes `op.progress(cb)` for streaming progress updates (e.g., `LanguageModel.generateResponseAsync` token streams).
- **Always `close()` IDisposable WinRT objects** in a `try/finally`. This frees the underlying COM resources promptly.
- **Pass `AbortSignal` for cancellation** when the underlying API supports it: `recognizer.recognizeTextFromImageAsync(imagePath, signal)`, `LanguageModel.createAsync(signal)`. Calling `controller.abort()` releases the awaiting Promise.

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

The first call to a WinRT method imported from `bindings/` will load `@microsoft/dynwinrt`, resolve the `.winmd` metadata, and invoke the COM method via libffi — all transparent to your code.

## Step 4 (optional): Regenerate after a metadata change

The generated `bindings/` files are committed-or-gitignored at your discretion (treat them like `package-lock.json` — generated, but stable enough to commit if you want diff visibility). Regenerate whenever:

- You bump a WinAppSDK / WinRT package version in `winapp.yaml`
- You add or remove entries in `winapp.jsBindings.packages` / `additionalWinmds` / `extraTypes` (in `package.json`)
- The codegen itself is upgraded (`npm update @microsoft/dynwinrt-codegen`)

In all cases, re-run codegen — it picks up the current `winapp.yaml` and `package.json` (neither file is mutated):

```bash
# Fast path: only re-runs dynwinrt-codegen against the cached lockfile.
# Use this after editing only `winapp.jsBindings` in package.json.
npx winapp node generate-bindings

# Full restore: also refreshes the lockfile (NuGet + cppwinrt headers).
# Use this whenever you change `winapp.yaml` (packages, sdkVersion, ...).
npx winapp restore
```

## Troubleshooting

**`Cannot find module './bindings'`**
The generator hasn't produced output yet. Re-run `npx winapp restore` and verify `bindings/index.js` exists.

**`MissingMethodException` / `Type not registered`**
A class your code imports is in a `.winmd` that isn't on the codegen's input. Check the `packages` list (or `additionalWinmds`) inside `winapp.jsBindings` in `package.json` — empty/omitted `packages` means "all installed packages participate", but if you've curated the list make sure the relevant package is there.

**`HRESULT 0x8007XXXX` at call time**
The metadata was emitted but the OS implementation isn't available — usually a missing OS feature (e.g., a Windows AI API on a non-Copilot+ PC) or missing capability declaration in `Package.appxmanifest`. The exception message preserves the WinRT error string from the COM layer.

**Bindings work in development but not after `electron-packager` / `electron-builder`**
Make sure `@microsoft/dynwinrt` is in your runtime `dependencies` (not just `devDependencies`) and that the packager's `asarUnpack` rules include the native binary. See [`packaging.md`](packaging.md) for the recommended config.

## Next steps

- **Reference** — [JS / TypeScript bindings for WinRT (`winapp.jsBindings`)](../../js-bindings.md) for the full `package.json` schema and advanced recipes (slice by package, cherry-pick types, ship a vendor `.winmd`).
- **CLI** — [`npx winapp init` reference](../../usage.md#init) and [`npx winapp restore` reference](../../usage.md#restore).
- **Runtime** — [`@microsoft/dynwinrt` on GitHub](https://github.com/microsoft/dynwinrt) for the libffi-based runtime that powers the generated bindings.
- **Package & ship** — [Packaging Your App](packaging.md) once you're ready to produce an MSIX for distribution.
