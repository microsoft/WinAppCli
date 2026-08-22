const fs = require('node:fs');
const path = require('node:path');
const { Worker } = require('node:worker_threads');

const architecture = { arm64: 'arm64', x64: 'x64', ia32: 'x86' }[process.arch];
if (!architecture) {
  throw new Error(`Unsupported Node.js architecture: ${process.arch}`);
}

const bootstrapDll =
  process.env.WINAPPSDK_BOOTSTRAP_DLL_PATH ??
  path.join(
    __dirname,
    '.winapp',
    'runtime',
    architecture,
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
  );
if (!fs.existsSync(bootstrapDll)) {
  throw new Error(
    `Windows App SDK bootstrap DLL was not found at ${bootstrapDll}. Run npm run prepare-runtime first.`
  );
}
process.env.WINAPPSDK_BOOTSTRAP_DLL_PATH = bootstrapDll;

const { initWinappsdk } = require('@microsoft/dynwinrt');
initWinappsdk(2, 2);

const worker = new Worker(path.join(__dirname, 'winui-worker.js'));

worker.on('message', (message) => {
  if (message?.type === 'ready') {
    console.log('WinUI 3 window is ready.');
  } else if (message?.type === 'error') {
    console.error(message.message);
    process.exitCode = 1;
  }
});

worker.on('error', (error) => {
  console.error(error);
  process.exitCode = 1;
});

worker.on('exit', (code) => {
  if (code === 0) {
    console.log('WinUI 3 window closed.');
  }
  process.exit(code);
});
