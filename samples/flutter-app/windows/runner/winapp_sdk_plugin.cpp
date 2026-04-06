#include "winapp_sdk_plugin.h"

#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>
#include <winrt/base.h>
#include <winrt/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.h>
#include <winrt/Microsoft.Windows.AppNotifications.h>
#include <winrt/Microsoft.Windows.AppNotifications.Builder.h>

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
        } else if (call.method_name() == "showNotification") {
          try {
            // Build a simple toast notification using Windows App SDK
            auto builder = winrt::Microsoft::Windows::AppNotifications::Builder::
                AppNotificationBuilder();
            builder.AddText(L"Hello from Flutter!");
            builder.AddText(L"This notification is powered by the Windows App SDK.");

            auto notification = builder.BuildNotification();
            auto manager = winrt::Microsoft::Windows::AppNotifications::
                AppNotificationManager::Default();
            manager.Show(notification);

            result->Success(flutter::EncodableValue(true));
          } catch (const winrt::hresult_error& e) {
            result->Error("WINRT_ERROR", winrt::to_string(e.message()));
          } catch (...) {
            result->Error("UNKNOWN_ERROR", "Failed to show notification");
          }
        } else {
          result->NotImplemented();
        }
      });

  // prevent channel destruction by releasing ownership
  channel.release();
}
