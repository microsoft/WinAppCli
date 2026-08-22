const path = require('node:path');
const { runtimePrepare } = require('@microsoft/winappcli');

const architecture = { arm64: 'arm64', x64: 'x64', ia32: 'x86' }[process.arch];
if (!architecture) {
  throw new Error(`Unsupported Node.js architecture: ${process.arch}`);
}

async function main() {
  const { stdout } = await runtimePrepare({
    version: '2.2.0',
    arch: architecture,
    output: path.join(__dirname, '.winapp', 'runtime', architecture),
    install: true,
    json: true,
  });

  const result = JSON.parse(stdout);
  console.log(`Windows App SDK ${result.runtimeVersion} is ready (${result.deploymentMode}).`);
  console.log(`Bootstrap DLL: ${result.bootstrapDllPath}`);
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = error.exitCode ?? 1;
});
