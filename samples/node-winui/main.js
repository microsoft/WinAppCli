const path = require('node:path');
const { Worker } = require('node:worker_threads');

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
