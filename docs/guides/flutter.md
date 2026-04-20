# Using winapp CLI with Flutter

For a complete working example, check out the [Flutter sample](../../samples/flutter-app) in this repository.

This guide demonstrates how to use `winappcli` with a Flutter application to add package identity and package your app as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard Flutter Windows build does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1. **Flutter SDK**: Install Flutter following the [official guide](https://docs.flutter.dev/install/quick).

2. **winapp CLI**: Install the `winapp` CLI via winget (or update if already installed):
    ```powershell
    winget install Microsoft.winappcli --source winget
    ```

## 1. Create a New Flutter App

Follow the guide at the official Flutter docs to create a new application and run it.

You should see the default Flutter counter app.

## 2. Update Code to Check Identity

We'll update the app to check if it's running with package identity. We'll use Dart FFI to call the Windows `GetCurrentPackageFamilyName` API.

First, add the `ffi` package:

```powershell
flutter pub add ffi
```

Next, replace the contents of `lib/main.dart` with the following code. This code attempts to retrieve the current package identity using the Windows API. If it succeeds, it displays the Package Family Name in the UI; otherwise, it shows "Not packaged".

```dart
import 'dart:ffi';
import 'dart:io' show Platform;

import 'package:ffi/ffi.dart';
import 'package:flutter/material.dart';

/// Returns the Package Family Name if running with package identity, or null.
String? getPackageFamilyName() {
  if (!Platform.isWindows) return null;

  final kernel32 = DynamicLibrary.open('kernel32.dll');
  final getCurrentPackageFamilyName = kernel32.lookupFunction<
      Int32 Function(Pointer<Uint32>, Pointer<Uint16>),
      int Function(
          Pointer<Uint32>, Pointer<Uint16>)>('GetCurrentPackageFamilyName');

  final length = calloc<Uint32>();
  try {
    // First call to get required buffer length
    final result =
        getCurrentPackageFamilyName(length, Pointer<Uint16>.fromAddress(0));
    if (result != 122) return null; // ERROR_INSUFFICIENT_BUFFER = 122

    // Second call with buffer to get the name
    final namePtr = calloc<Uint16>(length.value);
    try {
      final result2 = getCurrentPackageFamilyName(length, namePtr);
      if (result2 == 0) {
        return namePtr.cast<Utf16>().toDartString(); // ERROR_SUCCESS = 0
      }
      return null;
    } finally {
      calloc.free(namePtr);
    }
  } finally {
    calloc.free(length);
  }
}

void main() {
  runApp(const MyApp());
}

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Flutter Demo',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: Colors.deepPurple),
      ),
      home: const MyHomePage(title: 'Flutter Demo Home Page'),
    );
  }
}

class MyHomePage extends StatefulWidget {
  const MyHomePage({super.key, required this.title});

  final String title;

  @override
  State<MyHomePage> createState() => _MyHomePageState();
}

class _MyHomePageState extends State<MyHomePage> {
  int _counter = 0;
  late final String? _packageFamilyName;

  @override
  void initState() {
    super.initState();
    _packageFamilyName = getPackageFamilyName();
  }

  void _incrementCounter() {
    setState(() {
      _counter++;
    });
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        backgroundColor: Theme.of(context).colorScheme.inversePrimary,
        title: Text(widget.title),
      ),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Container(
              padding: const EdgeInsets.all(16),
              margin: const EdgeInsets.only(bottom: 24),
              decoration: BoxDecoration(
                color: _packageFamilyName != null
                    ? Colors.green.shade50
                    : Colors.orange.shade50,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: _packageFamilyName != null
                      ? Colors.green
                      : Colors.orange,
                ),
              ),
              child: Text(
                _packageFamilyName != null
                    ? 'Package Family Name:\n$_packageFamilyName'
                    : 'Not packaged',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyLarge,
              ),
            ),
            const Text('You have pushed the button this many times:'),
            Text(
              '$_counter',
              style: Theme.of(context).textTheme.headlineMedium,
            ),
          ],
        ),
      ),
      floatingActionButton: FloatingActionButton(
        onPressed: _incrementCounter,
        tooltip: 'Increment',
        child: const Icon(Icons.add),
      ),
    );
  }
}
```

## 3. Run Without Identity

Now, build and run the app as usual:

```powershell
flutter build windows
```

Run the executable directly (replace `flutter_app` with your project name if different):

```powershell
.\build\windows\x64\runner\Release\flutter_app.exe
```

> [!TIP]
> The build output is in the `x64` folder regardless of your machine's architecture — this is expected for Flutter's Windows build.

You should see the app with an orange "Not packaged" indicator. This confirms that the standard executable is running without any package identity.

## 4. Initialize Project with winapp CLI

The `winapp init` command sets up everything you need in one go: app manifest, assets, and optionally Windows App SDK headers for C++ development. The manifest defines your app's identity (name, publisher, version) which Windows uses to grant API access.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (derived from your project name)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Description**: Press Enter to accept the default (Windows Application)
- **Setup SDKs**: Select "Stable SDKs" to download Windows App SDK and generate C++ headers (needed for step 6)

This command will:
- Create `appxmanifest.xml` — the manifest that defines your app's identity
- Create `Assets` folder — icons required for MSIX packaging and Store submission
- Create a `.winapp` folder with Windows App SDK headers and libraries
- Create a `winapp.yaml` configuration file for pinning SDK versions

You can open `appxmanifest.xml` to further customize properties like the display name, publisher, and capabilities.

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, you can use `winapp run`. This registers a loose layout package (just like a real MSIX install) and launches the app in one step. No certificate or signing is needed for debugging.

1. **Build the app**:
    ```powershell
    flutter build windows
    ```

2. **Run with identity**:
    ```powershell
    winapp run .\build\windows\x64\runner\Release
    ```

> **Tip**: `winapp run` also registers the package on your system. This is why the MSIX may appear as "already installed" when you try to install it later in step 7. Use `winapp unregister` to clean up development packages when done.

You should now see the app with a green indicator showing:
```
Package Family Name: flutterapp.debug_xxxxxxxx
```
This confirms your app is running with a valid package identity!

> **Tip:** For advanced debugging workflows (attaching debuggers, IDE setup, startup debugging), see the [Debugging Guide](../debugging.md).

## 6. Using Windows App SDK (Optional)

If you selected to setup the SDKs during `winapp init`, you now have access to Windows App SDK C++ headers in the `.winapp/include` folder. Since Flutter's Windows runner is C++, you can call Windows App SDK APIs from native code and expose them to Dart via a method channel. If you just need package identity for distribution, you can skip to step 7.

Let's add a simple example that displays the Windows App Runtime version.

### Create the Native Plugin

Create `windows/runner/winapp_sdk_plugin.h`:

```cpp
#ifndef RUNNER_WINAPP_SDK_PLUGIN_H_
#define RUNNER_WINAPP_SDK_PLUGIN_H_

#include <flutter/flutter_engine.h>

// Registers a method channel for querying Windows App SDK info.
void RegisterWinAppSdkPlugin(flutter::FlutterEngine* engine);

#endif  // RUNNER_WINAPP_SDK_PLUGIN_H_
```

Create `windows/runner/winapp_sdk_plugin.cpp`:

```cpp
#include "winapp_sdk_plugin.h"

#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>
#include <winrt/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.h>

#include <string>

void RegisterWinAppSdkPlugin(flutter::FlutterEngine* engine) {
  auto channel = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      engine->messenger(), "com.example/winapp_sdk",
      &flutter::StandardMethodCodec::GetInstance());

  channel->SetMethodCallHandler(
      [](const flutter::MethodCall<flutter::EncodableValue>& call,
         std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        if (call.method_name() == "getRuntimeVersion") {
          try {
            // Flutter already initializes COM in main.cpp, so we skip
            // winrt::init_apartment() here — the apartment is already set up.
            auto version = winrt::Microsoft::Windows::ApplicationModel::
                WindowsAppRuntime::RuntimeInfo::AsString();
            std::string versionStr = winrt::to_string(version);
            result->Success(flutter::EncodableValue(versionStr));
          } catch (const winrt::hresult_error& e) {
            result->Error("WINRT_ERROR", winrt::to_string(e.message()));
          } catch (...) {
            result->Error("UNKNOWN_ERROR",
                          "Failed to get Windows App Runtime version");
          }
        } else {
          result->NotImplemented();
        }
      });

  // prevent channel destruction by releasing ownership
  channel.release();
}
```

### Update CMakeLists.txt

Edit `windows/runner/CMakeLists.txt` to make three changes. Find the `add_executable` block and add `"winapp_sdk_plugin.cpp"` to the source file list:

```cmake
add_executable(${BINARY_NAME} WIN32
  "flutter_window.cpp"
  "main.cpp"
  "utils.cpp"
  "win32_window.cpp"
  "winapp_sdk_plugin.cpp"       # <-- add this line
  "${FLUTTER_MANAGED_DIR}/generated_plugin_registrant.cc"
  "Runner.rc"
  "runner.exe.manifest"
)
```

Then add these two lines at the end of the file to link WinRT libraries and include the Windows App SDK headers:

```cmake
# Link Windows Runtime libraries for WinRT
target_link_libraries(${BINARY_NAME} PRIVATE "WindowsApp.lib")

# Windows App SDK headers from winapp CLI
target_include_directories(${BINARY_NAME} PRIVATE
  "${CMAKE_SOURCE_DIR}/../.winapp/include")
```

### Register the Plugin

In `windows/runner/flutter_window.cpp`, add the include at the top of the file with the other includes:

```cpp
#include "winapp_sdk_plugin.h"
```

Then find the `RegisterPlugins` call in `FlutterWindow::OnCreate()` and add `RegisterWinAppSdkPlugin` on the line right after it:

```cpp
  RegisterPlugins(flutter_controller_->engine());
  RegisterWinAppSdkPlugin(flutter_controller_->engine());  // <-- add this line
```

### Update main.dart

Add the following import at the top of `lib/main.dart`, alongside the existing imports:

```dart
import 'package:flutter/services.dart';
```

Add this function below the existing `getPackageFamilyName()` function (outside any class):

```dart
/// Queries the Windows App Runtime version via a native method channel.
Future<String?> getWindowsAppRuntimeVersion() async {
  if (!Platform.isWindows) return null;
  try {
    const channel = MethodChannel('com.example/winapp_sdk');
    final version = await channel.invokeMethod<String>('getRuntimeVersion');
    return version;
  } catch (_) {
    return null;
  }
}
```

In the `_MyHomePageState` class, add a new field next to the existing `_packageFamilyName`:

```dart
  late final String? _packageFamilyName;
  String? _runtimeVersion;         // <-- add this line
```

Update `initState()` to call the new function:

```dart
  @override
  void initState() {
    super.initState();
    _packageFamilyName = getPackageFamilyName();
    // Fetch the runtime version asynchronously
    getWindowsAppRuntimeVersion().then((version) {
      setState(() {
        _runtimeVersion = version;
      });
    });
  }
```

Finally, display the runtime version in the `build` method. Add this widget inside the `Column` children list, right after the `Container` that shows the package identity:

```dart
            if (_runtimeVersion != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 16),
                child: Text(
                  'Windows App Runtime: $_runtimeVersion',
                  style: Theme.of(context).textTheme.bodyLarge,
                ),
              ),
```

### Build and Run

Rebuild the application:

```powershell
flutter build windows
winapp run .\build\windows\x64\runner\Release
```

You should now see output like:
```
Package Family Name: flutterapp.debug_xxxxxxxx
Windows App Runtime: 8000.731.1532.0
```

The `.winapp/include` directory contains all the necessary headers for Windows App SDK, including:
- `winrt/` - WinRT C++ projection headers for accessing Windows Runtime APIs
- `Microsoft.UI.*.h` - WinUI 3 headers for modern UI components
- `MddBootstrap.h` - Windows App SDK bootstrapping
- `WindowsAppSDK-VersionInfo.h` - Version information
- And many more Windows App SDK components

For more advanced Windows App SDK usage, check out the [Windows App SDK documentation](https://learn.microsoft.com/windows/apps/windows-app-sdk/).

## 7. Package with MSIX

Once you're ready to distribute your app, you can package it as an MSIX using the same manifest.

### Prepare the Package Directory

First, build your application in release mode:

```powershell
flutter build windows
```

Then, create a directory with your release files:

```powershell
mkdir dist
copy .\build\windows\x64\runner\Release\* .\dist\ -Recurse
```

The Flutter Windows build output includes the executable, `flutter_windows.dll`, and a `data` folder — all of which are needed.

### Generate a Development Certificate

Before packaging, you need a development certificate for signing. Generate one if you haven't already:

```powershell
winapp cert generate --if-exists skip
```

### Sign and Pack

Now you can package and sign:

```powershell
winapp pack .\dist --cert .\devcert.pfx
```

> Note: The `pack` command automatically uses the `appxmanifest.xml` from your current directory and copies it to the target folder before packaging.

### Install the Certificate

Before you can install the MSIX package, you need to trust the development certificate on your machine. Run this command as administrator (you only need to do this once per certificate):

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run

> **Tip**: If you used `winapp run` in step 5, the package may already be registered on your system. Use `winapp unregister` first to remove the development registration, then install the release package.

Install the package by double-clicking the generated `.msix` file, or using PowerShell:

```powershell
Add-AppxPackage .\flutterapp.msix
```

> **Tip**: The MSIX filename includes the version and architecture (e.g., `flutterapplication1_1.0.0.0_x64.msix`). Check your directory for the exact filename. If you need to repackage after code changes, increment the `Version` in your `appxmanifest.xml` — Windows requires a higher version number to update an installed package.

## Tips

1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The [Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing) service is a great way to manage your certificates securely and integrate signing into your CI/CD pipeline.
3. The Microsoft Store will sign the MSIX for you, no need to sign before submission.

## Next Steps

- **Distribute via winget**: Submit your MSIX to the [Windows Package Manager Community Repository](https://github.com/microsoft/winget-pkgs)
- **Publish to the Microsoft Store**: Use `winapp store` to submit your package
- **Set up CI/CD**: Use the [`setup-WinAppCli`](https://github.com/microsoft/setup-WinAppCli) GitHub Action to automate packaging in your pipeline
- **Explore Windows APIs**: With package identity, you can now use [Notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart), [on-device AI](https://learn.microsoft.com/windows/ai/apis/), and other [identity-dependent APIs](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
