const { parentPort } = require('node:worker_threads');
const { roInitialize } = require('@microsoft/dynwinrt');
const {
  AppWindow,
  Button,
  DesktopWindowXamlSource,
  DispatcherQueueController,
  HorizontalAlignment,
  IVector_UIElement,
  Orientation,
  SolidColorBrush,
  StackPanel,
  TextBlock,
  VerticalAlignment,
  WindowsXamlManager,
} = require('#winapp/bindings');

const color = (r, g, b, a = 255) => ({ a, r, g, b });
const thickness = (left, top, right, bottom) => ({
  left,
  top,
  right,
  bottom,
});
const fontWeight = (weight) => ({ weight });
const brush = (r, g, b, a) =>
  SolidColorBrush.createInstanceWithColor(color(r, g, b, a));

function appendChildren(panel, ...children) {
  const collection = IVector_UIElement.from(panel.children._obj);
  for (const child of children) {
    collection.append(child);
  }
}

function createText(text, size, foreground, weight = 400) {
  const block = TextBlock.create();
  block.text = text;
  block.fontSize = size;
  block.fontWeight = fontWeight(weight);
  block.foreground = foreground;
  block.horizontalAlignment = HorizontalAlignment.Center;
  return block;
}

function createButton(label, background, foreground) {
  const button = Button.createInstance(null);
  button.content = createText(label, 16, foreground, 600);
  button.background = background;
  button.foreground = foreground;
  button.padding = thickness(18, 10, 18, 10);
  button.minWidth = 96;
  return button;
}

roInitialize(0);

try {
  const controller = DispatcherQueueController.createOnCurrentThread();
  const dispatcher = controller.dispatcherQueue;
  const xamlManager = WindowsXamlManager.initializeForCurrentThread();

  const appWindow = AppWindow.create();
  appWindow.title = 'WinUI 3 from Node.js';
  appWindow.resize({ width: 640, height: 440 });

  const xamlSource = DesktopWindowXamlSource.createInstance(null);
  xamlSource.initialize(appWindow.id);
  const resizeXamlContent = () => {
    const { width, height } = appWindow.clientSize;
    xamlSource.siteBridge.moveAndResize({ x: 0, y: 0, width, height });
  };

  const background = brush(243, 243, 243);
  const primary = brush(26, 26, 26);
  const secondary = brush(90, 90, 90);
  const accent = brush(0, 103, 192);
  const neutral = brush(225, 225, 225);
  const white = brush(255, 255, 255);
  const negative = brush(196, 43, 28);

  const root = StackPanel.createInstance(null);
  root.orientation = Orientation.Vertical;
  root.spacing = 16;
  root.padding = thickness(32, 32, 32, 32);
  root.background = background;
  root.horizontalAlignment = HorizontalAlignment.Stretch;
  root.verticalAlignment = VerticalAlignment.Stretch;

  const title = createText('Native WinUI 3', 30, primary, 600);
  const subtitle = createText(
    'Real controls created directly from JavaScript',
    15,
    secondary
  );
  const countText = createText('0', 72, accent, 700);
  const statusText = createText('Choose an action', 14, secondary);

  const buttons = StackPanel.createInstance(null);
  buttons.orientation = Orientation.Horizontal;
  buttons.spacing = 10;
  buttons.horizontalAlignment = HorizontalAlignment.Center;

  const decrementButton = createButton('-1', neutral, primary);
  const resetButton = createButton('Reset', neutral, primary);
  const incrementButton = createButton('+1', accent, white);
  appendChildren(buttons, decrementButton, resetButton, incrementButton);
  appendChildren(root, title, subtitle, countText, buttons, statusText);

  function updateCount(next, action) {
    countText.text = String(next);
    countText.foreground = next < 0 ? negative : accent;
    statusText.text = `${action}: count is now ${next}`;
  }

  globalThis.__winuiSubscriptions = [
    decrementButton.onClick(() => {
      updateCount((Number(countText.text) || 0) - 1, 'Decremented');
    }),
    resetButton.onClick(() => {
      updateCount(0, 'Reset');
    }),
    incrementButton.onClick(() => {
      updateCount((Number(countText.text) || 0) + 1, 'Incremented');
    }),
    appWindow.onChanged((_sender, args) => {
      if (args.didSizeChange) {
        resizeXamlContent();
      }
    }),
    appWindow.onClosing(() => {
      dispatcher.enqueueEventLoopExit();
    }),
  ];

  xamlSource.content = root;
  resizeXamlContent();
  appWindow.show();
  parentPort.postMessage({ type: 'ready' });

  // WinUI owns this worker thread while the window is open.
  dispatcher.runEventLoop();

  xamlSource.close();
  xamlManager.close();
  controller.shutdownQueue();
  process.exit(0);
} catch (error) {
  parentPort.postMessage({
    type: 'error',
    message: error?.stack || String(error),
  });
  throw error;
}
