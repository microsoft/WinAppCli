import 'dart:ffi';
import 'dart:io' show Platform;

import 'package:ffi/ffi.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// Returns the Package Family Name if running with package identity, or null.
String? getPackageFamilyName() {
  if (!Platform.isWindows) return null;

  final kernel32 = DynamicLibrary.open('kernel32.dll');
  final getCurrentPackageFamilyName = kernel32
      .lookupFunction<
        Int32 Function(Pointer<Uint32>, Pointer<Uint16>),
        int Function(Pointer<Uint32>, Pointer<Uint16>)
      >('GetCurrentPackageFamilyName');

  final length = calloc<Uint32>();
  try {
    // First call to get required buffer length
    final result = getCurrentPackageFamilyName(
      length,
      Pointer<Uint16>.fromAddress(0),
    );
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

/// Shows a Windows App SDK toast notification via a native method channel.
Future<void> showNotification() async {
  if (!Platform.isWindows) return;
  const channel = MethodChannel('com.example/winapp_sdk');
  await channel.invokeMethod('showNotification');
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
  String? _runtimeVersion;

  @override
  void initState() {
    super.initState();
    _packageFamilyName = getPackageFamilyName();
    _loadRuntimeVersion();
  }

  Future<void> _loadRuntimeVersion() async {
    final version = await getWindowsAppRuntimeVersion();
    if (mounted) {
      setState(() {
        _runtimeVersion = version;
      });
    }
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
            if (_runtimeVersion != null)
              Container(
                padding: const EdgeInsets.all(16),
                margin: const EdgeInsets.only(bottom: 24),
                decoration: BoxDecoration(
                  color: Colors.blue.shade50,
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(color: Colors.blue),
                ),
                child: Text(
                  'Windows App Runtime:\n$_runtimeVersion',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodyLarge,
                ),
              ),
            const Text('You have pushed the button this many times:'),
            Text(
              '$_counter',
              style: Theme.of(context).textTheme.headlineMedium,
            ),
            const SizedBox(height: 24),
            if (_packageFamilyName != null)
              ElevatedButton.icon(
                onPressed: showNotification,
                icon: const Icon(Icons.notifications),
                label: const Text('Show Notification'),
              )
            else
              const Text(
                'Run with package identity to enable notifications',
                style: TextStyle(color: Colors.grey),
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
