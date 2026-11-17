#include <windows.h>
#include <dwmapi.h>
#include <DispatcherQueue.h>
#include <windows.ui.composition.interop.h>
#include <windows.graphics.effects.interop.h>
#include <initguid.h>
#include <d2d1effects.h>
#include <memory>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.Foundation.Numerics.h>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Graphics::Effects;
using namespace Windows::UI::Composition;
using namespace Windows::UI::Composition::Desktop;
namespace effectsabi = ABI::Windows::Graphics::Effects;

struct BlurEffect : implements<BlurEffect, IGraphicsEffect, IGraphicsEffectSource, effectsabi::IGraphicsEffectD2D1Interop>
{
    IGraphicsEffectSource source{ nullptr };
    hstring name;
    hstring Name() const { return name; }
    void Name(hstring const& value) { name = value; }
    HRESULT __stdcall GetEffectId(GUID* id) noexcept final { *id = CLSID_D2D1GaussianBlur; return S_OK; }
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR, UINT*, effectsabi::GRAPHICS_EFFECT_PROPERTY_MAPPING*) noexcept final { return E_INVALIDARG; }
    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept final { *count = 3; return S_OK; }
    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept final
    {
        try {
            IInspectable property = index == 0 ? box_value(16.0f) : index == 1 ? box_value(uint32_t(1)) : index == 2 ? box_value(uint32_t(1)) : nullptr;
            if (!property) return E_INVALIDARG;
            auto boxed = property.as<Windows::Foundation::IPropertyValue>();
            copy_to_abi(boxed, *reinterpret_cast<void**>(value));
            return S_OK;
        } catch (...) { return to_hresult(); }
    }
    HRESULT __stdcall GetSource(UINT index, effectsabi::IGraphicsEffectSource** value) noexcept final
    {
        if (index != 0) return E_INVALIDARG;
        copy_to_abi(source, *reinterpret_cast<void**>(value));
        return S_OK;
    }
    HRESULT __stdcall GetSourceCount(UINT* count) noexcept final { *count = 1; return S_OK; }
};

struct HudVisual
{
    HWND window{};
    Windows::System::DispatcherQueueController queue{ nullptr };
    Compositor compositor{ nullptr };
    DesktopWindowTarget target{ nullptr };
    SpriteVisual visual{ nullptr };
    CompositionRoundedRectangleGeometry geometry{ nullptr };
    ~HudVisual() { target = nullptr; visual = nullptr; geometry = nullptr; compositor = nullptr; if (window) DestroyWindow(window); }
};
static thread_local HRESULT lastError = S_OK;

static LRESULT CALLBACK BackdropProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_NCHITTEST) return HTTRANSPARENT;
    if (message == WM_ERASEBKGND) return 1;
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

extern "C" __declspec(dllexport) void* __cdecl GlintHudCreate()
{
    try {
        lastError = S_OK;
        auto result = std::make_unique<HudVisual>();
        if (!Windows::System::DispatcherQueue::GetForCurrentThread()) {
            DispatcherQueueOptions options{ sizeof(options), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA };
            check_hresult(CreateDispatcherQueueController(options, reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(put_abi(result->queue))));
        }
        static bool registered = [] {
            WNDCLASSW wc{}; wc.lpfnWndProc = BackdropProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"Glint.CompositionBackdrop";
            return RegisterClassW(&wc) != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
        }();
        if (!registered) { lastError = HRESULT_FROM_WIN32(GetLastError()); return nullptr; }
        result->window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP,
            L"Glint.CompositionBackdrop", L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!result->window) { lastError = HRESULT_FROM_WIN32(GetLastError()); return nullptr; }
        BOOL enabled = TRUE;
        check_hresult(DwmSetWindowAttribute(result->window, 17, &enabled, sizeof(enabled))); // DWMWA_USE_HOSTBACKDROPBRUSH
        result->compositor = Compositor();
        auto interop = result->compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
        check_hresult(interop->CreateDesktopWindowTarget(result->window, FALSE, reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(put_abi(result->target))));
        auto effect = make_self<BlurEffect>();
        effect->source = CompositionEffectSourceParameter(L"background");
        auto factory = result->compositor.CreateEffectFactory(*effect);
        auto brush = factory.CreateBrush();
        brush.SetSourceParameter(L"background", result->compositor.CreateHostBackdropBrush());
        result->visual = result->compositor.CreateSpriteVisual();
        result->visual.Brush(brush);
        result->geometry = result->compositor.CreateRoundedRectangleGeometry();
        result->visual.Clip(result->compositor.CreateGeometricClip(result->geometry));
        result->target.Root(result->visual);
        return result.release();
    } catch (...) { lastError = to_hresult(); return nullptr; }
}
extern "C" __declspec(dllexport) long __cdecl GlintHudLastError() { return lastError; }

extern "C" __declspec(dllexport) int __cdecl GlintHudShow(void* handle, HWND foreground, int x, int y, int width, int height, float radius)
{
    try {
        auto p = static_cast<HudVisual*>(handle);
        if (!p || width <= 0 || height <= 0) return 0;
        p->visual.Size({ static_cast<float>(width), static_cast<float>(height) });
        p->geometry.Size({ static_cast<float>(width), static_cast<float>(height) });
        p->geometry.CornerRadius({ radius, radius });
        return SetWindowPos(p->window, foreground, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW) ? 1 : 0;
    } catch (...) { return 0; }
}
extern "C" __declspec(dllexport) void __cdecl GlintHudHide(void* handle) { auto p = static_cast<HudVisual*>(handle); if (p) ShowWindow(p->window, SW_HIDE); }
extern "C" __declspec(dllexport) void __cdecl GlintHudOpacity(void* handle, float opacity) { try { auto p = static_cast<HudVisual*>(handle); if (p) p->visual.Opacity(opacity); } catch (...) {} }
extern "C" __declspec(dllexport) void __cdecl GlintHudDestroy(void* handle) { delete static_cast<HudVisual*>(handle); }
