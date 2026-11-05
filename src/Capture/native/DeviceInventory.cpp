// Read-only inventory using the public DeckLink SDK 16 Windows COM interfaces.
// No IDeckLinkInput, EnableVideoInput, SetActive, SetInt or preferences writes.
#include <windows.h>
#include <oleauto.h>
#include <DeckLinkAPI.h>
#include "PciInventory.h"
#include "DirectShowCorrelation.h"
#include <iostream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

template<class T> struct ComPtr {
    T* value = nullptr;
    ~ComPtr() { if (value) value->Release(); }
    T* operator->() const { return value; }
};

static std::string quoted(BSTR text) {
    if (!text) return "null";
    auto length = SysStringLen(text);
    if (length > 512) return "null";
    int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, length, nullptr, 0, nullptr, nullptr);
    if (bytes == 0 && length != 0) return "null";
    std::string utf8(bytes, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, length, utf8.data(), bytes, nullptr, nullptr);
    std::ostringstream result;
    result << '"';
    for (unsigned char c : utf8) {
        if (c == '"' || c == '\\') result << '\\' << c;
        else if (c < 32) result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << unsigned(c);
        else result << c;
    }
    result << '"';
    return result.str();
}
static std::string identity(IDeckLinkProfileAttributes* attributes, BMDDeckLinkAttributeID key) {
    LONGLONG value = 0;
    if (!attributes || attributes->GetInt(key, &value) != S_OK) return "null";
    std::ostringstream result;
    result << "\"0x" << std::hex << std::setw(16) << std::setfill('0') << static_cast<ULONGLONG>(value) << '"';
    return result.str();
}
static std::string number(IDeckLinkProfileAttributes* attributes, BMDDeckLinkAttributeID key) {
    LONGLONG value = 0;
    return attributes && attributes->GetInt(key, &value) == S_OK ? std::to_string(value) : "null";
}
static std::string flag(IDeckLinkProfileAttributes* attributes, BMDDeckLinkAttributeID key) {
    BOOL value = FALSE;
    return attributes && attributes->GetFlag(key, &value) == S_OK ? (value ? "true" : "false") : "null";
}
static std::string profile(IDeckLinkProfileAttributes* attributes) {
    LONGLONG value = 0;
    if (!attributes || attributes->GetInt(BMDDeckLinkProfileID, &value) != S_OK) return "null";
    std::string result = "\"";
    for (int shift = 24; shift >= 0; shift -= 8) {
        auto c = static_cast<unsigned char>((value >> shift) & 255);
        if (c < 32 || c > 126 || c == '"' || c == '\\') return "null";
        result += char(c);
    }
    return result + '"';
}
static std::string hresult(HRESULT value) {
    std::ostringstream result;
    result << "\"0x" << std::hex << std::setw(8) << std::setfill('0') << static_cast<DWORD>(value) << '\"';
    return result.str();
}
static std::string modeValue(LONGLONG value) {
    std::string result = "\"";
    if (value >= 0 && static_cast<ULONGLONG>(value) <= 0xffffffffULL) {
        bool printable = true;
        for (int shift = 24; shift >= 0; shift -= 8) {
            auto c = static_cast<unsigned char>((value >> shift) & 255);
            if (c < 32 || c > 126) { printable = false; break; }
            if (c == '\"' || c == '\\') result += '\\';
            result += char(c);
        }
        if (printable) return result + '\"'; // Retain padded FourCCs such as "pal ".
    }
    std::ostringstream raw;
    raw << "\"0x" << std::hex << std::setw(16) << std::setfill('0') << static_cast<ULONGLONG>(value) << '\"';
    return raw.str();
}
static std::string statusJson(IDeckLink* device) {
    ComPtr<IDeckLinkStatus> status;
    HRESULT queried = device->QueryInterface(IID_IDeckLinkStatus, reinterpret_cast<void**>(&status.value));
    std::ostringstream result;
    result << "{\"interfaceHResult\":" << hresult(queried);
    if (queried != S_OK || !status.value) {
        result << ",\"videoInputSignalLocked\":null,\"videoInputSignalLockedHResult\":null"
            << ",\"detectedVideoInputMode\":null,\"detectedVideoInputModeHResult\":null"
            << ",\"currentVideoInputMode\":null,\"currentVideoInputModeHResult\":null}";
        return result.str();
    }
    // The SDK DeviceStatus sample queries these device observations without obtaining
    // IDeckLinkInput or enabling/starting capture. Never substitute defaults on failure:
    // false is meaningful only when GetFlag itself returned S_OK.
    BOOL locked = FALSE;
    HRESULT lockResult = status->GetFlag(bmdDeckLinkStatusVideoInputSignalLocked, &locked);
    LONGLONG detected = 0, current = 0;
    HRESULT detectedResult = status->GetInt(bmdDeckLinkStatusDetectedVideoInputMode, &detected);
    HRESULT currentResult = status->GetInt(bmdDeckLinkStatusCurrentVideoInputMode, &current);
    result << ",\"videoInputSignalLocked\":" << (lockResult == S_OK ? (locked ? "true" : "false") : "null")
        << ",\"videoInputSignalLockedHResult\":" << hresult(lockResult)
        << ",\"detectedVideoInputMode\":" << (detectedResult == S_OK ? modeValue(detected) : "null")
        << ",\"detectedVideoInputModeHResult\":" << hresult(detectedResult)
        << ",\"currentVideoInputMode\":" << (currentResult == S_OK ? modeValue(current) : "null")
        << ",\"currentVideoInputModeHResult\":" << hresult(currentResult) << '}';
    return result.str();
}
static std::string deviceJson(IDeckLink* device) {
    ComPtr<IDeckLinkProfileAttributes> attributes;
    device->QueryInterface(IID_IDeckLinkProfileAttributes, reinterpret_cast<void**>(&attributes.value));
    ComPtr<IDeckLinkConfiguration> configuration;
    device->QueryInterface(IID_IDeckLinkConfiguration, reinterpret_cast<void**>(&configuration.value));
    BSTR model = nullptr, display = nullptr, handle = nullptr;
    device->GetModelName(&model);
    device->GetDisplayName(&display);
    if (attributes.value) attributes->GetString(BMDDeckLinkDeviceHandle, &handle);
    LONGLONG selected = 0;
    bool hasSelected = configuration.value && configuration->GetInt(bmdDeckLinkConfigVideoInputConnection, &selected) == S_OK;
    std::ostringstream result;
    result << "{\"deviceHandle\":" << quoted(handle) << ",\"modelName\":" << quoted(model)
        << ",\"displayName\":" << quoted(display)
        << ",\"deviceGroupId\":" << identity(attributes.value, BMDDeckLinkDeviceGroupID)
        << ",\"persistentId\":" << identity(attributes.value, BMDDeckLinkPersistentID)
        << ",\"topologicalId\":" << identity(attributes.value, BMDDeckLinkTopologicalID)
        << ",\"deviceInterface\":" << number(attributes.value, BMDDeckLinkDeviceInterface)
        << ",\"supportsInputFormatDetection\":" << flag(attributes.value, BMDDeckLinkSupportsInputFormatDetection)
        << ",\"subDeviceIndex\":" << number(attributes.value, BMDDeckLinkSubDeviceIndex)
        << ",\"subDeviceCount\":" << number(attributes.value, BMDDeckLinkNumberOfSubDevices)
        << ",\"profileId\":" << profile(attributes.value)
        << ",\"videoInputConnections\":" << number(attributes.value, BMDDeckLinkVideoInputConnections)
        << ",\"selectedVideoInputConnection\":" << (hasSelected ? std::to_string(selected) : "null")
        << ",\"status\":" << statusJson(device) << '}';
    SysFreeString(model); SysFreeString(display); SysFreeString(handle);
    return result.str();
}
int main(int argc, char** argv) {
    bool correlate = argc == 2 && std::string(argv[1]) == "--pcie-correlate-idle";
    if (argc != 1 && !correlate) return 2;
    const auto pci = pciInventoryJson();
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) { std::cout << "{\"available\":false,\"devices\":[],\"error\":\"COM initialization failed.\",\"pciInventory\":" << pci << "}\n"; return 0; }
    int exitCode = 0;
    {
        ComPtr<IDeckLinkIterator> iterator;
        HRESULT created = CoCreateInstance(CLSID_CDeckLinkIterator, nullptr, CLSCTX_ALL, IID_IDeckLinkIterator, reinterpret_cast<void**>(&iterator.value));
        if (FAILED(created)) {
            std::cout << "{\"available\":false,\"devices\":[],\"error\":\"DeckLink SDK iterator unavailable. Check Desktop Video driver installation and account access.\",\"pciInventory\":" << pci << "}\n";
        } else {
            // Buffer the complete report: never publish a truncated/partly successful inventory.
            std::vector<std::string> devices;
            size_t sdkBytes = 0;
            bool sdkTruncated = false;
            for (;;) {
                ComPtr<IDeckLink> device;
                HRESULT next = iterator->Next(&device.value);
                if (next == S_FALSE) break;
                if (next != S_OK || !device.value || devices.size() >= 256) { exitCode = 1; break; }
                auto report = deviceJson(device.value);
                if (correlate && sdkBytes + report.size() > 24576) { sdkTruncated = true; break; }
                sdkBytes += report.size(); devices.push_back(report);
            }
            if (exitCode == 0) {
                std::ostringstream output;
                output << "{\"available\":true,\"devices\":[";
                for (size_t i = 0; i < devices.size(); ++i) { if (i) output << ','; output << devices[i]; }
                output << "],\"error\":null,\"pciInventory\":" << pci;
                if (correlate) output << ",\"sdkTruncated\":" << (sdkTruncated ? "true" : "false") << ",\"directShowCorrelation\":" << directShowCorrelationJson();
                output << "}\n";
                if (output.str().size() > 131072) exitCode = 1;
                else std::cout << output.str();
            }
        }
    }
    CoUninitialize();
    return exitCode;
}
