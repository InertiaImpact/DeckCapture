#include <windows.h>
#include <dshow.h>
#include <KsDeckLink.h>
#include "DirectShowCorrelation.h"
#include "PciInventory.h"
#include <iomanip>
#include <optional>
#include <sstream>
#include <vector>

namespace {
template<class T> struct ComPtr {
    T* value = nullptr;
    ~ComPtr() { if (value) value->Release(); }
    T* operator->() const { return value; }
};
std::string quote(const std::wstring& text) {
    if (text.empty() || text.size() > 512) return "null";
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return "null";
    std::string utf8(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), utf8.data(), size, nullptr, nullptr);
    std::ostringstream result; result << '"';
    for (unsigned char c : utf8) {
        if (c == '"' || c == '\\') result << '\\' << c;
        else if (c < 32) result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << unsigned(c);
        else result << c;
    }
    result << '"'; return result.str();
}
std::wstring property(IPropertyBag* bag, const wchar_t* name) {
    VARIANT value; VariantInit(&value);
    std::wstring result;
    if (bag && bag->Read(name, &value, nullptr) == S_OK && value.vt == VT_BSTR && value.bstrVal && SysStringLen(value.bstrVal) <= 512)
        result.assign(value.bstrVal, SysStringLen(value.bstrVal));
    VariantClear(&value); return result;
}
std::string identity(std::optional<DWORD> value) {
    if (!value) return "null";
    std::ostringstream result; result << "\"0x" << std::hex << std::setw(16) << std::setfill('0') << *value << '"';
    return result.str();
}
std::string monikerJson(IMoniker* moniker) {
    ComPtr<IPropertyBag> bag;
    moniker->BindToStorage(nullptr, nullptr, IID_IPropertyBag, reinterpret_cast<void**>(&bag.value));
    auto name = property(bag.value, L"FriendlyName");
    auto path = property(bag.value, L"DevicePath");
    auto resolution = blackmagicPciInstanceForInterface(path);
    auto pnp = resolution.pciInstanceId;
    std::optional<DWORD> persistent, topology;
    std::string error;
    if (pnp.empty()) error = resolution.error;
    else {
        // Binding may access the card, which is why this is a fixed, separately reserved idle probe.
        // Do not create a graph, enumerate pins, set a format, run a filter, or change preferences.
        ComPtr<IBaseFilter> filter;
        HRESULT bound = moniker->BindToObject(nullptr, nullptr, IID_IBaseFilter, reinterpret_cast<void**>(&filter.value));
        if (FAILED(bound)) error = "Blackmagic filter binding failed.";
        else {
            ComPtr<IKsDeckLink> decklink;
            HRESULT queried = filter->QueryInterface(IID_IKsDeckLink, reinterpret_cast<void**>(&decklink.value));
            if (FAILED(queried)) {
                // SDK documentation describes access through the capture interface. Query that
                // inherited interface when a driver does not publish the base IID independently.
                ComPtr<IKsDeckLinkCapture> capture;
                queried = filter->QueryInterface(IID_IKsDeckLinkCapture, reinterpret_cast<void**>(&capture.value));
                if (SUCCEEDED(queried)) { decklink.value = capture.value; decklink.value->AddRef(); }
            }
            if (FAILED(queried)) error = "Bound filter did not expose the SDK DeckLink ID interface.";
            else {
                DWORD value = 0;
                if (decklink->GetPersistentID(&value) == S_OK) persistent = value;
                if (decklink->GetTopologicalID(&value) == S_OK) topology = value;
                if (!persistent && !topology) error = "DeckLink filter returned neither identity attribute.";
            }
        }
    }
    std::ostringstream result;
    result << "{\"friendlyName\":" << quote(name) << ",\"devicePath\":" << quote(path)
        << ",\"pnpInstanceId\":" << quote(pnp) << ",\"topologicalId\":" << identity(topology)
        << ",\"persistentId\":" << identity(persistent)
        << ",\"interfaceInstanceId\":" << quote(resolution.interfaceInstanceId) << ",\"interfaceAncestorInstanceIds\":[";
    for (size_t i = 0; i < resolution.ancestorInstanceIds.size(); ++i) { if (i) result << ','; result << quote(resolution.ancestorInstanceIds[i]); }
    result << "],\"interfaceAncestryTruncated\":" << (resolution.ancestryTruncated ? "true" : "false") << ",\"error\":";
    if (error.empty()) result << "null"; else result << '"' << error << '"';
    result << '}'; return result.str();
}
}

std::string directShowCorrelationJson() {
    ComPtr<ICreateDevEnum> enumerator;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_ICreateDevEnum, reinterpret_cast<void**>(&enumerator.value))))
        return "{\"available\":false,\"filters\":[],\"truncated\":false,\"error\":\"DirectShow device enumeration unavailable.\"}";
    ComPtr<IEnumMoniker> monikers;
    HRESULT enumeration = enumerator->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &monikers.value, 0);
    if (enumeration != S_OK && enumeration != S_FALSE)
        return "{\"available\":false,\"filters\":[],\"truncated\":false,\"error\":\"DirectShow video enumeration failed.\"}";
    std::vector<std::string> filters;
    size_t bytes = 0;
    bool truncated = false;
    if (monikers.value) for (size_t index = 0; ; ++index) {
        if (index >= 256) { truncated = true; break; }
        ComPtr<IMoniker> moniker;
        HRESULT next = monikers->Next(1, &moniker.value, nullptr);
        if (next == S_FALSE) break;
        if (next != S_OK || !moniker.value)
            return "{\"available\":false,\"filters\":[],\"truncated\":false,\"error\":\"DirectShow video enumeration was interrupted.\"}";
        auto report = monikerJson(moniker.value);
        if (bytes + report.size() > 27648) { truncated = true; break; }
        bytes += report.size(); filters.push_back(report);
    }
    std::ostringstream result; result << "{\"available\":true,\"filters\":[";
    for (size_t i = 0; i < filters.size(); ++i) { if (i) result << ','; result << filters[i]; }
    result << "],\"truncated\":" << (truncated ? "true" : "false") << ",\"error\":null}";
    return result.str();
}
