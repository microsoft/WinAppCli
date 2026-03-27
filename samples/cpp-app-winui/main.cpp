#include <windows.h>
#include <appmodel.h>
#include <string>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Windows.UI.Xaml.Interop.h>

// Forward-declare bootstrap APIs to avoid header conflicts with C++/WinRT
extern "C" {
    HRESULT __stdcall MddBootstrapInitialize2(
        UINT32 majorMinorVersion,
        PCWSTR versionTag,
        PACKAGE_VERSION minVersion,
        UINT32 options) noexcept;
    void __stdcall MddBootstrapShutdown() noexcept;
}

// Windows App SDK 1.8 version constants
constexpr UINT32 kWinAppSdkMajorMinor = 0x00010008;
// OnPackageIdentity_NOOP: skip bootstrap when running packaged (via winapp run)
constexpr UINT32 kBootstrapOptions_OnPackageIdentity_NOOP = 0x0010;

using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;

struct App : ApplicationT<App, Microsoft::UI::Xaml::Markup::IXamlMetadataProvider>
{
    // Minimal IXamlMetadataProvider (no custom XAML types)
    Microsoft::UI::Xaml::Markup::IXamlType GetXamlType(Windows::UI::Xaml::Interop::TypeName const&)
    {
        return nullptr;
    }
    Microsoft::UI::Xaml::Markup::IXamlType GetXamlType(hstring const&)
    {
        return nullptr;
    }
    com_array<Microsoft::UI::Xaml::Markup::XmlnsDefinition> GetXmlnsDefinitions()
    {
        return {};
    }

    void OnLaunched(LaunchActivatedEventArgs const&)
    {
        m_window = Window();

        auto panel = StackPanel();
        panel.HorizontalAlignment(HorizontalAlignment::Center);
        panel.VerticalAlignment(VerticalAlignment::Center);
        panel.Spacing(16);

        auto title = TextBlock();
        title.Text(L"WinUI 3 CMAKE C++ Sample");
        title.FontSize(24);
        title.HorizontalAlignment(HorizontalAlignment::Center);

        auto infoText = TextBlock();
        infoText.Text(L"Click the button to check package identity");
        infoText.HorizontalAlignment(HorizontalAlignment::Center);
        infoText.TextWrapping(TextWrapping::Wrap);

        auto button = Button();
        button.Content(box_value(L"Check Identity"));
        button.HorizontalAlignment(HorizontalAlignment::Center);
        button.Click([infoText](auto&&, auto&&) {
            UINT32 length = 0;
            LONG result = GetCurrentPackageFamilyName(&length, nullptr);

            if (result == ERROR_INSUFFICIENT_BUFFER) {
                std::wstring familyName(length, L'\0');
                result = GetCurrentPackageFamilyName(&length, familyName.data());

                if (result == ERROR_SUCCESS) {
                    familyName.resize(wcslen(familyName.c_str()));
                    infoText.Text(hstring(L"Package Family Name: ") + hstring(familyName));
                } else {
                    infoText.Text(L"Error retrieving Package Family Name");
                }
            } else {
                infoText.Text(L"Not packaged \u2014 run with 'winapp run' for identity");
            }
        });

        panel.Children().Append(title);
        panel.Children().Append(button);
        panel.Children().Append(infoText);

        m_window.Content(panel);
        m_window.Title(L"C++ WinUI 3 Sample");
        m_window.Activate();
    }

private:
    Window m_window{ nullptr };
};

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int)
{
    try
    {
        // COM must be initialized before the bootstrap
        init_apartment(apartment_type::single_threaded);

        // Initialize the Windows App SDK for unpackaged execution.
        // When running packaged (winapp run), OnPackageIdentity_NOOP skips this.
        PACKAGE_VERSION minVer{};
        minVer.Version = 0x1F4002A304760000u;
        HRESULT hr = MddBootstrapInitialize2(
            kWinAppSdkMajorMinor, L"", minVer,
            kBootstrapOptions_OnPackageIdentity_NOOP);
        if (FAILED(hr))
        {
            MessageBoxW(nullptr,
                L"Failed to initialize Windows App SDK.\n"
                L"Make sure the Windows App SDK runtime is installed.",
                L"WinUI 3 Error", MB_ICONERROR);
            return static_cast<int>(hr);
        }

        Application::Start([](auto&&) { make<App>(); });

        MddBootstrapShutdown();
    }
    catch (hresult_error const& ex)
    {
        MessageBoxW(nullptr, ex.message().c_str(), L"WinUI 3 Error", MB_ICONERROR);
        return static_cast<int>(ex.code());
    }
    return 0;
}
