const { parentPort } = require('node:worker_threads');
const {
  DynWinRtType,
  DynWinRtValue,
  roInitialize,
} = require('@microsoft/dynwinrt');
const {
  Application,
  Border,
  Button,
  ComboBox,
  ElementTheme,
  Grid,
  HorizontalAlignment,
  IMap_Object_Object,
  IVector_UIElement,
  MicaBackdrop,
  Orientation,
  PropertyValue,
  SolidColorBrush,
  StackPanel,
  TextAlignment,
  TextBlock,
  TextWrapping,
  TitleBarTheme,
  VerticalAlignment,
  Window,
} = require('#winapp/bindings');

const thickness = (left, top, right, bottom) => ({
  left,
  top,
  right,
  bottom,
});
const fontWeight = (weight) => ({ weight });

function appendChildren(panel, ...children) {
  const collection = panel.children.as(IVector_UIElement);
  for (const child of children) {
    collection.append(child);
  }
}

const cornerRadius = (radius) => ({
  topLeft: radius,
  topRight: radius,
  bottomRight: radius,
  bottomLeft: radius,
});
const createBrush = (a, r, g, b) =>
  new SolidColorBrush({ a, r, g, b });

function createText(text, size, weight = 400, centered = false) {
  const block = new TextBlock();
  block.text = text;
  block.fontSize = size;
  block.fontWeight = fontWeight(weight);
  if (centered) {
    block.horizontalAlignment = HorizontalAlignment.Center;
    block.textAlignment = TextAlignment.Center;
  }
  return block;
}

function createButton(label) {
  const button = new Button();
  button.content = createText(label, 16, 600);
  button.minWidth = 96;
  return button;
}

function reportError(error) {
  parentPort.postMessage({
    type: 'error',
    message: error?.stack || String(error),
  });
}

roInitialize(0);

let app;
let exitCode = 1;

Application.start(() => {
  try {
    app = Application.create(() => {
      try {
        const window = new Window();
        window.title = 'WinUI 3 from Node.js';
        window.systemBackdrop = new MicaBackdrop();

        const appWindow = window.appWindow;
        if (!appWindow) {
          throw new Error('WinUI Window did not expose an AppWindow.');
        }
        const titleBar = appWindow.titleBar;
        if (!titleBar) {
          throw new Error('AppWindow did not expose a title bar.');
        }

        const resources =
          Application.current.resources.as(IMap_Object_Object);
        const getResource = (key) =>
          resources.lookup(PropertyValue.createString(key));

        const root = new Grid();
        root.padding = thickness(56, 32, 56, 32);

        const content = new StackPanel();
        content.orientation = Orientation.Vertical;
        content.spacing = 16;
        content.verticalAlignment = VerticalAlignment.Center;

        const eyebrow = createText('NODE.JS + WINUI 3', 12, 600);
        const title = createText('Fluent Counter', 34, 600);
        const subtitle = createText(
          'A native WinUI 3 window composed entirely from JavaScript through dynwinrt.',
          15
        );
        subtitle.textWrapping = TextWrapping.Wrap;

        const themeOptions = [
          {
            label: 'System',
            contentTheme: ElementTheme.Default,
            titleBarTheme: TitleBarTheme.UseDefaultAppMode,
          },
          {
            label: 'Light',
            contentTheme: ElementTheme.Light,
            titleBarTheme: TitleBarTheme.Light,
          },
          {
            label: 'Dark',
            contentTheme: ElementTheme.Dark,
            titleBarTheme: TitleBarTheme.Dark,
          },
        ];
        const themePicker = new ComboBox();
        themePicker.header = createText('Theme', 13, 600);
        themePicker.minWidth = 180;
        themePicker.horizontalAlignment = HorizontalAlignment.Left;
        themePicker.itemsSource = DynWinRtValue.createVector(
          themeOptions.map(({ label }) => PropertyValue.createString(label)),
          DynWinRtType.object()
        );
        themePicker.selectedIndex = 0;
        titleBar.preferredTheme = themeOptions[0].titleBarTheme;

        let count = 0;
        const countCard = new Border();
        countCard.padding = thickness(32, 24, 32, 24);
        countCard.cornerRadius = cornerRadius(12);
        countCard.borderThickness = thickness(1, 1, 1, 1);
        countCard.margin = thickness(0, 10, 0, 10);

        const countPanel = new StackPanel();
        countPanel.orientation = Orientation.Vertical;
        countPanel.spacing = 2;

        const countLabel = createText('CURRENT VALUE', 11, 600, true);
        const countText = createText(String(count), 56, 600, true);
        appendChildren(countPanel, countLabel, countText);
        countCard.child = countPanel;

        const buttons = new StackPanel();
        buttons.orientation = Orientation.Horizontal;
        buttons.spacing = 10;
        buttons.horizontalAlignment = HorizontalAlignment.Center;

        const decrementButton = createButton('-1');
        const resetButton = createButton('Reset');
        const incrementButton = createButton('+1');
        incrementButton.style = getResource('AccentButtonStyle');
        appendChildren(buttons, decrementButton, resetButton, incrementButton);

        const statusText = createText('Ready to count.', 13, 400, true);
        const footer = createText(
          'Application + Window  |  Fluent resources  |  Mica',
          11,
          400,
          true
        );

        appendChildren(root, content);
        appendChildren(
          content,
          eyebrow,
          title,
          subtitle,
          themePicker,
          countCard,
          buttons,
          statusText,
          footer
        );

        function applyThemeResources() {
          if (root.actualTheme === ElementTheme.Dark) {
            countCard.background = createBrush(13, 255, 255, 255);
            countCard.borderBrush = createBrush(20, 255, 255, 255);
            eyebrow.foreground = createBrush(255, 96, 205, 255);
          } else {
            countCard.background = createBrush(179, 255, 255, 255);
            countCard.borderBrush = createBrush(24, 0, 0, 0);
            eyebrow.foreground = createBrush(255, 0, 95, 184);
          }
        }

        function updateCount(next, action) {
          count = next;
          countText.text = String(count);
          statusText.text = `${action}: count is now ${count}`;
        }

        globalThis.__winuiState = {
          app,
          window,
          root,
          subscriptions: [
            decrementButton.onClick(() => {
              updateCount(count - 1, 'Decremented');
            }),
            resetButton.onClick(() => {
              updateCount(0, 'Reset');
            }),
            incrementButton.onClick(() => {
              updateCount(count + 1, 'Incremented');
            }),
            themePicker.onSelectionChanged(() => {
              const selected = themeOptions[themePicker.selectedIndex];
              if (selected) {
                root.requestedTheme = selected.contentTheme;
                titleBar.preferredTheme = selected.titleBarTheme;
                statusText.text = `Theme changed to ${selected.label}.`;
              }
            }),
            root.onActualThemeChanged(applyThemeResources),
            window.onClosed(() => {
              Application.current?.exit();
            }),
            root.onceLoaded(() => {
              applyThemeResources();
              const scale = root.xamlRoot?.rasterizationScale ?? 1;
              appWindow.resize({
                width: Math.round(680 * scale),
                height: Math.round(600 * scale),
              });
            }),
          ],
        };

        window.content = root;
        applyThemeResources();
        window.activate();
        exitCode = 0;
        parentPort.postMessage({ type: 'ready' });
      } catch (error) {
        reportError(error);
        Application.current?.exit();
      }
    });
  } catch (error) {
    reportError(error);
    Application.current?.exit();
  }
});

process.exit(exitCode);
