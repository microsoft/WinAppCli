# A new way to bring native Windows APIs to JavaScript — introducing dynamic API projections for Node.js

![Public preview banner. Title: "A new way to bring native Windows APIs to JavaScript — introducing dynamic API projections for Node.js." Tagline: "Notifications, Phi Silica, clipboard, and more, straight from JavaScript. No native addon, no node-gyp." A code snippet on the right shows LanguageModel and TextSummarizer being used from the generated bindings.](./assets/blog-banner.png)

You're building an Electron app for Windows and want to add on-device AI features like text summarization, image description, and OCR, right from Copilot+ PCs. But when you look for how to call these Windows APIs from JavaScript, you hit a wall. The usual answer is to write a C++ or C# native addon just to reach a handful of APIs, and you repeat that work every time you need another one.

We're excited to introduce a public preview of **a dynamic Windows Runtime API (WinRT) projection for Node.js**. It lets your Electron app, or a plain Node.js process, call the modern Windows API surface directly from JavaScript or TypeScript. Install one npm package, get typed bindings for the Windows features you want. No native addon, no `node-gyp`, no C++ wrapper to maintain. 🚀

## What makes this different

The Windows Runtime already has static language projections for [C++/WinRT](https://github.com/microsoft/cppwinrt), [C#/WinRT](https://github.com/microsoft/cswinrt), [`windows-rs`](https://github.com/microsoft/windows-rs), and [PyWinRT](https://github.com/pywinrt/pywinrt). This new projection adds JavaScript to that list, targeting the **Node.js runtime**, with a different implementation approach: instead of a hand-authored native addon per API, it uses a codegen + shared runtime split. That means no per-API C++ to write or maintain, no `node-gyp` toolchain in your project, no Electron-version-specific rebuilds, and new Windows APIs light up as soon as their metadata ships.

- **JavaScript bindings, not a per-class native addon.** Codegen produces `.js` + `.d.ts` for supported Windows API patterns.
- **One shared prebuilt runtime.** `@microsoft/dynwinrt`, installed from npm, dispatches all the calls at execution time.
- **Regenerate, don't rebuild.** New Windows API metadata ships → rerun codegen → updated bindings. No hand-authored bindings to wait for.

## See it in action

We've already used these bindings to power [**Electron on Windows Gallery**](https://github.com/microsoft/electron-on-windows-gallery), an open-source Electron app that showcases the range of native Windows functionality reachable from Electron. It ships interactive samples covering the current Windows on-device AI APIs (text generation, summarization, rewriting, text-to-table, image description, OCR, image scaling, object extraction, and object removal) alongside JavaScript sample code, API documentation, and getting-started guides for building your own Electron-on-Windows features.

<p align="center">
  <img src="./assets/electron-gallery-samples.gif" width="720" alt="Electron on Windows Gallery running several on-device AI samples end-to-end: text summarization, OCR, object remover, and image description, each driven by a handful of JavaScript against the generated bindings." />
</p>

## What you can build

In this post we'll walk through two common Windows API scenarios from JavaScript:

- **Native notifications** with `AppNotificationBuilder` / `AppNotificationManager`
- **Phi Silica on-device AI** with `LanguageModel` / `TextSummarizer` (Copilot+ PCs)

These are just entry points. The same bindings reach a much broader surface of Windows Runtime APIs from JavaScript, including the full on-device AI stack shipped on Copilot+ PCs (text generation, summarization, rewriting, text-to-table, image description, text recognition, image scaling, object extraction, and object removal), file pickers and storage (`FileOpenPicker`, `StorageFile`), image decoding (`BitmapDecoder`), rich clipboard (`Clipboard`, `HtmlFormatHelper`), and custom model inference via `WinML`. If it's a non-UI WinRT type described in `.winmd` metadata, you can reach it. That includes your own WinRT components. Just extend the `winapp.jsBindings` config to generate bindings for any additional namespaces or `.winmd` files.

Both walkthroughs are written entirely in JavaScript.

## What's in the box

Three npm packages, designed to be used together:

- **[`@microsoft/dynwinrt`](https://www.npmjs.com/package/@microsoft/dynwinrt)**: the shared native runtime that dispatches Windows API calls at execution time. Prebuilt for x64 and arm64 Windows.
- **[`@microsoft/dynwinrt-codegen`](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)**: the code generator that reads `.winmd` metadata and emits JavaScript bindings with TypeScript types (`.js` + `.d.ts`).
- **[`@microsoft/winappcli`](https://www.npmjs.com/package/@microsoft/winappcli)**: the Windows App Development CLI. It manages the NuGet packages that ship Windows API metadata, runs codegen, pins the matching `@microsoft/dynwinrt` runtime, and handles debug package identity.

You only install `@microsoft/winappcli`; it brings in the other two for you.

## Adding it to your Electron app

### Project setup

In your Electron app, install the CLI and initialize:

```bash
npm install --save-dev @microsoft/winappcli
npx winapp init . --use-defaults --add-js-bindings
```

The WinApp CLI sets up the manifest and SDKs, adds `@microsoft/dynwinrt` + `@microsoft/dynwinrt-codegen` to your `package.json`, and writes typed bindings to `.winapp/bindings/`. It also writes a `winapp.jsBindings` config block to your `package.json` where you can extend the generated surface with additional namespaces or `.winmd` files. See the [file picker guide](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-file-picker.md) for a detailed configuration example.

Both walkthroughs below (`AppNotificationManager` and Phi Silica) use APIs that require package identity. To unblock those APIs during development, grant your Electron dev build a temporary identity:

```bash
npx winapp node add-electron-debug-identity
```

Rerun `npx winapp node add-electron-debug-identity` whenever the manifest, exe path, or identity fields change. For APIs that don't require package identity, you can start Electron normally with `npm start` and skip this step. See the [debug identity guide](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/setup.md#step-5-understanding-debug-identity) for the full model, including which API categories require identity.

### Show a rich Windows notification

Electron can already show a basic notification, but Windows App SDK notifications let you use richer Windows-native toast features like progress bars, actions, inputs, and scenarios. Here's a progress notification from the Electron main process:

```js
const {
  AppNotificationBuilder,
  AppNotificationManager,
  AppNotificationProgressBar,
} = require('./.winapp/bindings/index.js');

const progress = AppNotificationProgressBar
  .create()
  .setTitle('Processing with Windows AI')
  .setStatus('Running locally')
  .setValue(0.65)
  .setValueStringOverride('65%');

AppNotificationManager.default_.show(
  AppNotificationBuilder
    .create()
    .addText('Windows AI task running')
    .addText('This notification uses a native Windows progress bar.')
    .addProgressBar(progress)
    .buildNotification()
);
```
Run the app, and Windows fires the same native toast you'd get from a C#/C++ app, including the progress bar:

![Windows toast notification from an Electron app titled "test-electron-app", reading "Windows AI task running" and showing a 65% progress bar labeled "Processing with Windows AI."](./assets/electron-toast-hello-from-electron.png)

Full walk-through in the [Show a notification from JavaScript](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-notification.md) guide.

### Run Phi Silica on-device AI

You can also add on-device AI to Electron directly from your app's JavaScript, using **Phi Silica**, a local language model that ships with Windows. **This requires a [Copilot+ PC](https://learn.microsoft.com/en-us/windows/ai/npu-devices/).**

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

Before running, add the `systemAIModels` restricted capability to `Package.appxmanifest`:

```xml
<Capabilities>
  <rescap:Capability Name="systemAIModels" />
</Capabilities>
```

Then refresh debug identity:

```bash
npx winapp node add-electron-debug-identity
```

Full walk-through in the [Call Phi Silica from JavaScript](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-phi-silica.md) guide.

Running the snippet above in an Electron main process, the summary streams into the terminal chunk by chunk, followed by the final `Done:` line:

![Terminal running electron-forge start: the Phi Silica summary streams in one partial chunk at a time via op.progress(), followed by a final "Done:" line with the complete summary.](./assets/phi-silica-console.gif)

## Extending to Windows SDK and beyond

By default the WinApp CLI feeds codegen the supported Windows App SDK API surface (UI-only packages like `Microsoft.WindowsAppSDK.WinUI` and `Microsoft.Web.WebView2` are excluded). To pull in Windows SDK APIs as well, list the entry-point classes you want in `package.json`. For example, add the Windows Clipboard API:

```jsonc
{
  "winapp": {
    "jsBindings": {
      "additionalWinmds": [
        {
          "namespace": "Windows.ApplicationModel.DataTransfer",
          "classes": ["Clipboard", "HtmlFormatHelper"]
        }
      ]
    }
  }
}
```

Then regenerate bindings:

```bash
npx winapp node generate-bindings
```

The codegen transitively pulls in dependent types, so you only list the entry-point classes.

Now your Electron main process can write richer Windows clipboard content. For example, HTML that pastes as formatted text in Word, Outlook, or Teams:

```js
const {
  Clipboard,
  DataPackage,
  HtmlFormatHelper,
} = require('./.winapp/bindings/index.js');

const html = HtmlFormatHelper.createHtmlFormat(`
  <h2>Hello from Electron</h2>
  <p>This was copied as <strong>HTML</strong> via Windows Clipboard APIs.</p>
  <p><a href="https://github.com/microsoft/winappCli">Open WinApp CLI on GitHub</a></p>
`);

const data = DataPackage.create();
data.setHtmlFormat(html);

Clipboard.setContent(data);
Clipboard.flush();
```

`HtmlFormatHelper.createHtmlFormat` wraps your markup with the CF_HTML header Windows expects, so any HTML-aware target renders it as rich text.

<p align="center">
  <img src="./assets/clipboard-html-demo.gif" width="720" alt="Running the snippet from the Electron main process and pasting into Word: the copied content renders as a formatted heading, bold text, and a clickable link, exactly the way the source HTML was written." />
</p>

For third-party WinRT components, or to include a package the WinApp CLI doesn't ship by default, point the entry directly at a `.winmd` file: `{ "winmdPath": "path/to/Foo.winmd", "namespace": "Foo.Bar", "classes": ["Baz"] }`. This generates the typed bindings; running the component at runtime still requires shipping its DLL and registering activation in your app's `Package.appxmanifest`.

## Plain Node.js: a dev-mode quick-start

Not using Electron? These bindings work from plain Node.js too. If you just want to try a Windows API on your own machine without packaging an MSIX, set up a project and point it at your existing `node.exe`:

```powershell
mkdir my-winrt-experiment; cd my-winrt-experiment
npm init -y
npm install --save-dev @microsoft/winappcli
npx winapp init . --use-defaults --add-js-bindings

# winapp run needs an .exe inside the project. Alias .local-node to your Node install.
New-Item -ItemType Junction -Path .\.local-node -Target (Split-Path (Get-Command node).Source)
```

We'll call Phi Silica again from Node.js, so add the same `systemAIModels` capability to `Package.appxmanifest`. Then in `app.js`, use `TextRewriter` to polish a sentence:

```js
const { roInitialize } = require('@microsoft/dynwinrt');
roInitialize(1); // MTA

const {
  AIFeatureReadyState,
  LanguageModel,
  TextRewriter,
  TextRewriteTone,
} = require('./.winapp/bindings/index.js');

async function main() {
  const readyState = LanguageModel.getReadyState();
  if (readyState === AIFeatureReadyState.NotReady) {
    await LanguageModel.ensureReadyAsync();
  }

  const model = await LanguageModel.createAsync();
  try {
    const rewriter = TextRewriter.createInstance(model);
    const result = await rewriter.rewriteAsync(
      'WinApp CLI makes it easier to use Windows APIs from JavaScript.',
      TextRewriteTone.Professional
    );
    console.log(result.text);
  } finally {
    model.close();
  }
}

main().catch(console.error);
```

Run the script under a registered package identity in one shot:

```powershell
npx winapp run . --exe .local-node\node.exe --with-alias --args "app.js" --unregister-on-exit
```

The rewritten text prints straight to your terminal. Under the hood, `winapp run` registers the loose-layout package, launches `.local-node\node.exe app.js` with package identity and the Windows App SDK runtime graph, and unregisters on exit (`--unregister-on-exit`). `--with-alias` uses the manifest's execution alias so stdout streams back to the launching terminal (without it, packaged apps run detached).

For an iteration-friendly variant using a persistent execution alias (`mynode.exe app.js` from any terminal) and packaging notes, see the full [Plain Node.js dev-mode guide](https://github.com/microsoft/dynwinrt/blob/main/docs/guides/node/dev-mode.md).

## How it works, briefly

`dynwinrt-codegen` runs during `winapp init` / `restore` / `generate-bindings` and turns `.winmd` metadata into JavaScript wrappers with TypeScript types. No native code is generated per class. At execution time, `@microsoft/dynwinrt` invokes the underlying COM vtables directly, handling the WinRT plumbing (HSTRINGs, HRESULTs to JavaScript exceptions, async operations to Promises, collections, structs, enums, delegates) transparently.

These bindings target non-UI Windows Runtime APIs such as AI, storage, notifications, networking, and similar system capabilities, not UI hosting like XAML / WinUI or WebView2.

For the full design, see [`dynwinrt/design.md`](https://github.com/microsoft/dynwinrt/blob/main/design.md).

## Final thoughts and feedback

This is public preview, and there's a lot we still want to sharpen. If a Windows API doesn't shape well in JS, a TypeScript type feels off, or a scenario you care about isn't covered yet, please file feedback. We're actively deciding what to invest in next.

The WinApp CLI and the underlying projection live in **two different repos**. File feedback in whichever fits your issue:

- **CLI ergonomics, bindings generation, docs, samples** → [`microsoft/winappCli`](https://github.com/microsoft/winappCli/issues)
- **Runtime, code generator, type support, TypeScript declarations** → [`microsoft/dynwinrt`](https://github.com/microsoft/dynwinrt/issues)

### WinApp CLI

- Repo and full command reference: [`microsoft/winappCli`](https://github.com/microsoft/winappCli)
- Install: [`@microsoft/winappcli`](https://www.npmjs.com/package/@microsoft/winappcli) on npm
- Electron getting-started guides: [setup](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/setup.md) · [file picker](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-file-picker.md) · [notification](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-notification.md) · [Phi Silica](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-phi-silica.md) · [WinML](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/js-winml.md)
- Debug identity for Electron: [Setup guide → Understanding Debug Identity](https://github.com/microsoft/winappCli/blob/main/docs/guides/electron/setup.md#step-5-understanding-debug-identity)

### dynwinrt (the projection itself)

- Repo, design notes, and benchmarks: [`microsoft/dynwinrt`](https://github.com/microsoft/dynwinrt)

We're excited to see what you build using the Node.js projection, `@microsoft/dynwinrt`, and the WinApp CLI. Happy coding! 🎉
