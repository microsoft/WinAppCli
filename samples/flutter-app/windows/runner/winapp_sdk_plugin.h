#ifndef RUNNER_WINAPP_SDK_PLUGIN_H_
#define RUNNER_WINAPP_SDK_PLUGIN_H_

#include <flutter/flutter_engine.h>

// Registers a method channel for querying Windows App SDK info.
void RegisterWinAppSdkPlugin(flutter::FlutterEngine* engine);

#endif  // RUNNER_WINAPP_SDK_PLUGIN_H_
