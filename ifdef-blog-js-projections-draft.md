# Introducing a dynamic WinRT projection for Node.js

We've been working on something new for Electron developers on Windows: **a dynamic WinRT projection for Node.js** that lets Electron and Node.js apps call many non-UI WinRT APIs — APIs described by `.winmd` metadata in the Windows App SDK or Windows SDK — directly from JS or TypeScript. No app-specific native addon, no `node-gyp` build step, no C++ or C# wrapper in your project. 🚀

Just like C++/WinRT, C#/WinRT, and PyWinRT project the Windows Runtime into their respective languages, this brings that projection model to JavaScript. The difference is what it does *not* do: it doesn't generate native code per class. The codegen step produces typed JavaScript (`.js` + `.d.ts`) for supported WinRT patterns; a single shared prebuilt runtime — installed from npm — handles calls at execution time. When new supported WinRT APIs ship as `.winmd` metadata, you can regenerate bindings without rebuilding a per-SDK native wrapper or waiting for hand-authored bindings.

The projection is currently in **public preview**. The motivation is simple: for most Electron developers, the bar for using a Windows API is plain `npm install` — and writing a C++ or C# addon just to fire a notification has always sat well above that bar. This is our attempt to close the gap. Give it a try in your project and let us know where it still falls short.

## What's in the box

Three npm packages, designed to be used together:

- **[`@microsoft/dynwinrt`](https://www.npmjs.com/package/@microsoft/dynwinrt)** — the runtime that powers the generated bindings, dispatching WinRT calls at execution time. Prebuilt for x64 and arm64 Windows, so it installs from npm like any other package — no compiler, no `node-gyp`.
- **[`@microsoft/dynwinrt-codegen`](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator that reads `.winmd` metadata and emits typed `.js` + `.d.ts`.
- **[`@microsoft/winappcli`](https://www.npmjs.com/package/@microsoft/winappcli)** — the Windows App Development CLI. It manages the NuGet packages that ship WinRT metadata, runs codegen, pins the matching `@microsoft/dynwinrt` runtime, and handles debug package identity.

You only install `@microsoft/winappcli`; it brings in the other two for you.

## What you can build

In this post we'll walk through two common Windows API scenarios from JavaScript:

- **Native notifications** with `AppNotificationBuilder` / `AppNotificationManager`
- **Phi Silica on-device AI** with `LanguageModel` / `TextSummarizer` (Copilot+ PCs)

Both are written entirely in JavaScript. The same pattern extends to file pickers, WinML execution-provider discovery, and other WinRT surfaces — full walk-throughs live in the [Electron guides](https://github.com/microsoft/winappCli/tree/main/docs/guides/electron).

## How to add the projection to your Electron app

### Project setup

In your Electron app, install the CLI and initialize:

```bash
npm install --save-dev @microsoft/winappcli
npx winapp init . --use-defaults --add-js-bindings
```

`winapp` sets up the manifest and SDKs, adds `@microsoft/dynwinrt` + `@microsoft/dynwinrt-codegen` to your `package.json`, and writes typed bindings to `.winapp/bindings/`. For APIs that require package identity, like notifications and Phi Silica, run `npx winapp node add-electron-debug-identity` before starting Electron.

### Show a native Windows notification

The notification sample is the smallest. From the Electron main process:

```js
const {
  AppNotificationBuilder,
  AppNotificationManager,
} = require('./.winapp/bindings/index.js');

AppNotificationManager.default_.show(
  AppNotificationBuilder
    .create()
    .addText('Hello from Electron!')
    .addText('Powered by the Windows App SDK.')
    .buildNotification()
);
```
Run it, and Windows fires the same native toast you'd get from a C#/C++ app:

![Windows toast notification from an Electron app titled "test-electron-app", reading "Hello from Electron!" and "This notification is powered by the Windows App SDK!"](./assets/electron-toast-hello-from-electron.png)

Full walk-through in the [Show a notification from JavaScript](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-notification.md) guide.

### Run Phi Silica on-device AI

On-device AI is the same shape — no extra ceremony, no addon to maintain:

```js
const {
  AIFeatureReadyState, LanguageModel, TextSummarizer,
} = require('./.winapp/bindings/index.js');

if (LanguageModel.getReadyState() === AIFeatureReadyState.NotReady) {
  await LanguageModel.ensureReadyAsync();
}

const model = await LanguageModel.createAsync();
try {
  const op = TextSummarizer
    .createInstance(model)
    .summarizeParagraphAsync('Some long paragraph...');

  // Stream partial output as the model generates it
  op.progress((partial) => {
    process.stdout.write(partial);
  });

  const result = await op;
  console.log('\nDone:', result.text);
} finally {
  model.close();
}
```

Phi Silica needs a Copilot+ PC. Before running, add the `systemAIModels` restricted capability to `Package.appxmanifest`:

```xml
<Capabilities>
  <rescap:Capability Name="systemAIModels" />
</Capabilities>
```

Then refresh debug identity with `npx winapp node add-electron-debug-identity`. Full walk-through in the [Call Phi Silica from JavaScript](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-phi-silica.md) guide.

Running the snippet above in an Electron main process, the summary streams into the DevTools console chunk by chunk, followed by the final `Done:` line:

![Electron DevTools console: the Phi Silica summary streams in one partial chunk at a time via op.progress(), followed by a final "Done:" line with the complete summary.](./assets/phi-silica-console.gif)

The snippet above is the minimal shape. To see the projection driving a full app end-to-end, take a look at [**Electron on Windows Gallery**](https://github.com/microsoft/electron-on-windows-gallery) — an open-source sample gallery powered by this same setup, with samples for text generation, summarization, rewriting, OCR, image description, image scaling, and object extraction. Here are a few of the samples in action:

![Electron on Windows Gallery running several on-device AI samples end-to-end: text summarization, OCR, object remover, and image description — each driven by a handful of JavaScript against the generated bindings.](./assets/electron-gallery-samples.gif)

## Extending to Windows SDK and beyond

By default `winapp` feeds codegen the supported Windows App SDK WinRT surface (UI-only packages like `Microsoft.WindowsAppSDK.WinUI` and `Microsoft.Web.WebView2` are excluded — see the scope note below). To pull in Windows SDK classes (for example to open a `FileOpenPicker`, decode an image with `BitmapDecoder`, or talk to `Windows.Web.Http.HttpClient`), add them to `package.json`:

```jsonc
{
  "winapp": {
    "jsBindings": {
      "additionalWinmds": [
        { "namespace": "Windows.Storage",          "classes": ["StorageFile"] },
        { "namespace": "Windows.Graphics.Imaging", "classes": ["BitmapDecoder"] }
      ]
    }
  }
}
```

Then `npx winapp node generate-bindings`. The codegen transitively pulls in dependent types — you only list the entry-point classes.

For third-party WinRT components, or to include a package `winapp` doesn't ship by default, point the entry directly at a `.winmd` file: `{ "winmdPath": "path/to/Foo.winmd", "namespace": "Foo.Bar", "classes": ["Baz"] }`.

## Plain Node.js: a dev-mode quick-start

Not on Electron? The same projections work from a plain Node.js process too. For local prototyping — trying a WinRT API on your own box without shipping an MSIX — register a loose-layout package and give it a command-line alias:

```powershell
mkdir my-winrt-experiment; cd my-winrt-experiment
npm init -y
npm install --save-dev @microsoft/winappcli
npx winapp init . --use-defaults --add-js-bindings

# Copy Node into the project so we can launch this app-specific copy
# without touching the system Node.
mkdir .local-node
copy (Get-Command node).Source .\.local-node\node.exe

# Add an execution alias, then register the loose-layout package.
npx winapp manifest add-alias --name mynode.exe --manifest .\Package.appxmanifest
npx winapp run . --exe .local-node\node.exe --no-launch
```

Write `app.js`:

```js
const { roInitialize } = require('@microsoft/dynwinrt');
roInitialize(1); // MTA

const {
  AppNotificationBuilder,
  AppNotificationManager,
} = require('./.winapp/bindings/index.js');

AppNotificationManager.default_.show(
  AppNotificationBuilder.create()
    .addText('Hello from Node.js!')
    .buildNotification()
);
```

Run it through the alias:

```powershell
mynode.exe app.js
```

The toast fires from Node.js, but Windows is launching it through the registered package: `mynode.exe` resolves to `.local-node\node.exe`, passes `app.js` as the argument, and starts the process with package identity and the Windows App SDK runtime graph.

When you're done experimenting, unregister the development package:

```powershell
npx winapp unregister
```

> **This is a dev-mode convenience.** `winapp run` registers a loose-layout package on your local machine so you can iterate without building an MSIX. When you're ready to distribute, package and sign the same app layout with `winapp pack` + `winapp sign`; the generated bindings and JavaScript code carry forward.

## How it works, briefly

`dynwinrt-codegen` runs during `winapp init` / `restore` / `generate-bindings` and turns `.winmd` metadata into typed JavaScript wrappers — no native code is generated per class. At execution time, `@microsoft/dynwinrt` invokes the underlying COM vtables directly, handling the WinRT plumbing (HSTRINGs, HRESULTs → JavaScript exceptions, async operations → Promises, collections, structs, enums, delegates) transparently.

The projection targets non-UI WinRT APIs such as AI, storage, notifications, networking, and similar system capabilities — not UI hosting like XAML / WinUI or WebView2.

For the full design, see [`dynwinrt/design.md`](https://github.com/microsoft/dynwinrt/blob/main/design.md).

## Final thoughts

This is public preview, and there's a lot we still want to sharpen. If a WinRT class doesn't shape well in JS, a TypeScript type feels off, or a scenario you care about isn't covered yet, please file feedback — we're actively deciding what to invest in next.

## Resources and feedback

### winapp CLI

- Repo and full command reference: [`microsoft/winappCli`](https://github.com/microsoft/winappCli)
- Install: [`@microsoft/winappcli`](https://www.npmjs.com/package/@microsoft/winappcli) on npm
- Electron getting-started guides: [setup](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/setup.md) · [file picker](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-file-picker.md) · [notification](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-notification.md) · [Phi Silica](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-phi-silica.md) · [WinML](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-winml.md)
- Debug identity for Electron: [Setup guide → Understanding Debug Identity](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/setup.md#step-5-understanding-debug-identity)
- File bugs / feature requests: [winappCli issues](https://github.com/microsoft/winappCli/issues)

### dynwinrt (the projection itself)

- Install: [`@microsoft/dynwinrt`](https://www.npmjs.com/package/@microsoft/dynwinrt) · [`@microsoft/dynwinrt-codegen`](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen) on npm
- Repo, design notes, and benchmarks: [`microsoft/dynwinrt`](https://github.com/microsoft/dynwinrt)
- File bugs / feedback: [dynwinrt issues](https://github.com/microsoft/dynwinrt/issues)

We're excited to see what you build using the Node.js projection, `@microsoft/dynwinrt`, and the `winapp` CLI. Happy coding! 🎉
