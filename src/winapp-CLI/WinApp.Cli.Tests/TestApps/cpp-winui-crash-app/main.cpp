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

extern "C" {
    HRESULT __stdcall MddBootstrapInitialize2(
        UINT32 majorMinorVersion,
        PCWSTR versionTag,
        PACKAGE_VERSION minVersion,
        UINT32 options) noexcept;
    void __stdcall MddBootstrapShutdown() noexcept;
}

constexpr UINT32 kWinAppSdkMajorMinor = 0x00010008;
constexpr UINT32 kBootstrapOptions_OnPackageIdentity_NOOP = 0x0010;

using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;

// Helper to trigger crash types
static void CrashAccessViolation()
{
    volatile int* p = nullptr;
    *p = 42;
}

static void CrashStackOverflow(int depth)
{
    volatile char buf[4096]{};
    buf[0] = static_cast<char>(depth);
    CrashStackOverflow(depth + 1);
}

static void CrashCppException()
{
    throw std::runtime_error("Intentional C++ crash for testing");
}

struct App : ApplicationT<App, Microsoft::UI::Xaml::Markup::IXamlMetadataProvider>
{
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
        panel.Padding(ThicknessHelper::FromUniformLength(40));
        panel.Spacing(16);

        auto title = TextBlock();
        title.Text(L"C++ WinUI 3 Crash Test");
        title.FontSize(24);

        auto subtitle = TextBlock();
        subtitle.Text(L"Click a button to trigger a crash for testing winapp run --debug-output");

        // Access Violation button
        auto avButton = Button();
        avButton.Content(box_value(L"Access Violation (null pointer write)"));
        avButton.Click([](auto&&, auto&&) {
            CrashAccessViolation();
        });

        // Stack Overflow button
        auto soButton = Button();
        soButton.Content(box_value(L"Stack Overflow (infinite recursion)"));
        soButton.Click([](auto&&, auto&&) {
            CrashStackOverflow(0);
        });

        // C++ Exception button
        auto cppButton = Button();
        cppButton.Content(box_value(L"C++ Exception (std::runtime_error)"));
        cppButton.Click([](auto&&, auto&&) {
            CrashCppException();
        });

        // Timed crash button
        auto timedButton = Button();
        timedButton.Content(box_value(L"Timed Crash (3 seconds)"));
        auto statusText = TextBlock();
        statusText.Text(L"Ready");

        timedButton.Click([statusText](auto&&, auto&&) mutable {
            statusText.Text(L"Crashing in 3 seconds...");
            auto timer = DispatcherTimer();
            timer.Interval(std::chrono::seconds(3));
            timer.Tick([timer, statusText](auto&&, auto&&) mutable {
                timer.Stop();
                CrashAccessViolation();
            });
            timer.Start();
        });

        panel.Children().Append(title);
        panel.Children().Append(subtitle);
        panel.Children().Append(avButton);
        panel.Children().Append(soButton);
        panel.Children().Append(cppButton);
        panel.Children().Append(timedButton);
        panel.Children().Append(statusText);

        m_window.Content(panel);
        m_window.Title(L"C++ Crash Test");
        m_window.Activate();
    }

private:
    Window m_window{ nullptr };
};

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int)
{
    try
    {
        init_apartment(apartment_type::single_threaded);

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
                L"Crash Test Error", MB_ICONERROR);
            return static_cast<int>(hr);
        }

        Application::Start([](auto&&) { make<App>(); });

        MddBootstrapShutdown();
    }
    catch (hresult_error const& ex)
    {
        MessageBoxW(nullptr, ex.message().c_str(), L"Crash Test Error", MB_ICONERROR);
        return static_cast<int>(ex.code());
    }
    return 0;
}
