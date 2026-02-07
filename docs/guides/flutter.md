# Using winapp CLI with Flutter

For a complete working example, check out the [Flutter sample](../../samples/flutter-app) in this repository.

This guide demonstrates how to use `winappcli` with a Flutter application to add package identity and package your app as an MSIX.

Package identity is a core concept in the Windows app model. It allows your application to access specific Windows APIs (like Notifications, Security, AI APIs, etc), have a clean install/uninstall experience, and more.

A standard Flutter Windows build does not have package identity. This guide shows how to add it for debugging and then package it for distribution.

## Prerequisites

1. **Flutter SDK**: Install Flutter following the [official guide](https://docs.flutter.dev/install/quick).

2. **winapp CLI**: Install the `winapp` CLI via winget:
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
.\build\windows\x64\runner\Release\flutter_app.exe
```

You should see the app with an orange "Not packaged" indicator. This confirms that the standard executable is running without any package identity.

## 4. Initialize Project with winapp CLI

The `winapp init` command sets up everything you need in one go: app manifest, assets, development certificate, and optionally Windows App SDK headers for C++ development.

Run the following command and follow the prompts:

```powershell
winapp init
```

When prompted:
- **Package name**: Press Enter to accept the default (flutter-app)
- **Publisher name**: Press Enter to accept the default or enter your name
- **Version**: Press Enter to accept 1.0.0.0
- **Entry point**: Press Enter to accept the default (flutter_app.exe)
- **Setup SDKs**: Select "Stable SDKs" to download Windows App SDK and generate C++ headers

This command will:
- Create `appxmanifest.xml` and `Assets` folder for your app identity
- Generate a development certificate (`devcert.pfx`) for signing
- Create a `.winapp` folder with Windows App SDK headers and libraries
- Create a `winapp.yaml` configuration file for pinning sdk versions

You can open `appxmanifest.xml` to further customize properties like the display name, publisher, and capabilities.

## 5. Debug with Identity

To test features that require identity (like Notifications) without fully packaging the app, you can use `winapp create-debug-identity`. This applies a temporary identity to your executable using the manifest we just generated.

1. **Build the app**:
    ```powershell
    flutter build windows
    ```

2. **Apply Debug Identity**:
    ```powershell
    winapp create-debug-identity .\build\windows\x64\runner\Release\flutter_app.exe
    ```

3. **Run the executable**:
    ```powershell
    .\build\windows\x64\runner\Release\flutter_app.exe
    ```

You should now see the app with a green indicator showing:
```
Package Family Name: flutter-app_xxxxxxxx
```
This confirms your app is running with a valid package identity!

> **Note**: After running `flutter clean` or rebuilding, you'll need to re-run `create-debug-identity` since the executable is replaced.

## 6. Using Windows App SDK (Optional)

If you selected to setup the SDKs during `winapp init`, you now have access to Windows App SDK C++ headers in the `.winapp/include` folder. Since Flutter's Windows runner is C++, you can call Windows App SDK APIs from native code and expose them to Dart via a method channel.

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

Edit `windows/runner/CMakeLists.txt` to add the new source file, include the Windows App SDK headers, and link the required libraries:

```cmake
# Add the new source file to the executable
add_executable(${BINARY_NAME} WIN32
  "flutter_window.cpp"
  "main.cpp"
  "utils.cpp"
  "win32_window.cpp"
  "winapp_sdk_plugin.cpp"
  "${FLUTTER_MANAGED_DIR}/generated_plugin_registrant.cc"
  "Runner.rc"
  "runner.exe.manifest"
)

# ... existing settings ...

# Link Windows Runtime libraries for WinRT
target_link_libraries(${BINARY_NAME} PRIVATE "WindowsApp.lib")

# Windows App SDK headers from winapp CLI
target_include_directories(${BINARY_NAME} PRIVATE
  "${CMAKE_SOURCE_DIR}/../.winapp/include")
```

### Register the Plugin

In `windows/runner/flutter_window.cpp`, include the header and register the plugin:

```cpp
#include "winapp_sdk_plugin.h"

// In FlutterWindow::OnCreate(), after RegisterPlugins:
RegisterPlugins(flutter_controller_->engine());
RegisterWinAppSdkPlugin(flutter_controller_->engine());
```

### Update main.dart

Add a method channel call in Dart to query the runtime version and display it:

```dart
import 'package:flutter/services.dart';

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

Call it in `initState()` and display it in the UI alongside the package identity indicator.

### Build and Run

Rebuild the application:

```powershell
flutter build windows
winapp create-debug-identity .\build\windows\x64\runner\Release\flutter_app.exe
.\build\windows\x64\runner\Release\flutter_app.exe
```

You should now see output like:
```
Package Family Name: flutter-app_xxxxxxxx
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

### Sign and Pack

Since `winapp init` already generated the development certificate, you can proceed directly to packaging:

```powershell
winapp pack .\dist --cert .\devcert.pfx
```

> Note: The `pack` command automatically uses the `appxmanifest.xml` from your current directory and copies it to the target folder before packaging.

### Install the Certificate

Before you can install the MSIX package, you need to install the development certificate. Run this command as administrator (you only need to do this once):

```powershell
winapp cert install .\devcert.pfx
```

### Install and Run

Install the package by double-clicking the generated `flutter-app.msix` file, or using PowerShell:

```powershell
Add-AppxPackage .\flutter-app.msix
```

### Tips

1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate.
2. The [Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing) service is a great way to manage your certificates securely and integrate signing into your CI/CD pipeline.
3. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
