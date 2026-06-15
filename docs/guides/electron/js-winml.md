<!-- mslearn: true -->
# Run WinML from JavaScript (JS bindings)

This guide shows a lightweight WinML flow from Electron JavaScript. It uses generated JS bindings for Windows App SDK ML provider discovery, and `onnxruntime-node` for ONNX inference. You don't need to create a C# native addon.

## Prerequisites

Before starting this guide, make sure you've:
- Completed the [development environment setup](setup.md).
- Added an ONNX model to your project, for example `models/model.onnx`.

Install ONNX Runtime for Node:

```bash
npm install onnxruntime-node
```

> [!NOTE]
> `onnxruntime-node` is a native npm dependency maintained by ONNX Runtime. This path avoids writing your own C# addon, but ONNX Runtime still provides the inference engine.
>
> The full Electron Gallery sample also uses JS bindings to decode image files with `StorageFile` and `BitmapDecoder`, then runs inference in an Electron utility process. This guide keeps the sample smaller and focuses on the Windows App SDK ML + ONNX Runtime handoff.

## Step 1: Confirm WinML bindings

The Windows App SDK package you installed during [setup](setup.md) transitively depends on `Microsoft.WindowsAppSDK.ML`, so the WinML APIs are already in your generated bindings. Sanity-check that `.winapp/bindings/index.js` exports `ExecutionProviderCatalog` from `Microsoft.Windows.AI.MachineLearning`:

```bash
node -e "console.log(Object.keys(require('./.winapp/bindings/index.js')).filter(k => k.startsWith('ExecutionProvider')))"
```

You should see `[ 'ExecutionProvider', 'ExecutionProviderCatalog', 'ExecutionProviderReadyState' ]`.

## Step 2: Register execution providers

Use the Windows App SDK ML catalog to discover and register certified ONNX Runtime execution providers:

```js
// src/index.js (Electron main, CommonJS)
const { ExecutionProviderCatalog } = require('../.winapp/bindings/index.js');

async function registerWinMlExecutionProviders() {
  const catalog = ExecutionProviderCatalog.getDefault();
  const providers = await catalog.registerCertifiedAsync();

  return providers.toArray().map((provider) => ({
    name: provider.name,
    readyState: provider.readyState,
    libraryPath: provider.libraryPath,
  }));
}
```

## Step 3: Run ONNX inference

Create an ONNX Runtime session after registering providers. This example uses a caller-provided tensor so the guide can stay focused on the WinML setup:

```js
const ort = require('onnxruntime-node');
const path = require('node:path');

async function runModel(inputData, inputShape) {
  const providers = await registerWinMlExecutionProviders();
  console.log('Registered WinML execution providers:', providers);

  const modelPath = path.join(__dirname, '..', 'models', 'model.onnx');
  const session = await ort.InferenceSession.create(modelPath, {
    executionProviders: [{ name: 'dml', deviceId: 0 }, 'cpu'],
    graphOptimizationLevel: 'all',
  });

  const inputName = session.inputNames[0];
  const outputName = session.outputNames[0];
  const input = new ort.Tensor('float32', inputData, inputShape);
  const outputs = await session.run({ [inputName]: input });

  return outputs[outputName].data;
}
```

The example tries DirectML first and falls back to CPU. For NPU-specific flows, use the registered provider list to choose the ONNX Runtime execution provider that matches your model and hardware.

Call it from your main process after your app starts:

```js
const createWindow = async () => {
  // ... existing window creation code ...

  // Replace this with preprocessing that matches your model.
  const inputData = new Float32Array(1 * 3 * 224 * 224);
  const output = await runModel(inputData, [1, 3, 224, 224]);

  console.log('Model output:', output);
};
```

## Step 4: Run it

```bash
npm start
```

If you need a complete image-classification pipeline, use the same pattern as the Electron Gallery: decode the image with generated bindings (`StorageFile`, `BitmapDecoder`, `BitmapTransform`), convert pixels into the tensor shape your model expects, and run `onnxruntime-node` in a utility process so model loading and inference don't block the Electron main process.

## Next Steps

Congratulations! You're now running WinML execution providers and ONNX Runtime from JavaScript — no C# addon required. 🎉

Now you're ready to:
- **[Package Your App for Distribution](packaging.md)** — produce an MSIX you can ship (the `@microsoft/dynwinrt` runtime is already in your `dependencies`).

Or explore other guides:
- **[Show a Notification from JavaScript](js-notification.md)** — show a Windows App SDK notification through JS bindings.
- **[Call Windows APIs from JavaScript](js-file-picker.md)** — pick a file and read its image dimensions using JS bindings.
- **[Call Phi Silica from JavaScript](js-phi-silica.md)** — summarize text with Windows App SDK AI through JS bindings.
- **[Creating a WinML Addon](winml-addon.md)** — native C# addon counterpart.
- **[Getting Started Overview](index.md)** — return to the main guide.

### Additional Resources

- **[winapp CLI Documentation](../../usage.md)** — full CLI reference (`init`, `restore`, `node generate-bindings`).
- **[Sample Electron App](../../../samples/electron/)** — complete working example, including JS bindings.
- **[@microsoft/dynwinrt](https://github.com/microsoft/dynwinrt)** — the runtime that powers the generated bindings.
- **[@microsoft/dynwinrt-codegen](https://www.npmjs.com/package/@microsoft/dynwinrt-codegen)** — the code generator.
