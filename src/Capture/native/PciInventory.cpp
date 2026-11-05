#include <windows.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <initguid.h>
#include <devpkey.h>
#include "PciInventory.h"
#include <algorithm>
#include <cwctype>
#include <iomanip>
#include <optional>
#include <sstream>
#include <vector>

namespace {
constexpr size_t MaximumPropertyBytes = 16384;
constexpr size_t MaximumNodes = 64;
constexpr size_t MaximumParents = 8;
constexpr size_t MaximumJsonBytes = 65536;
bool propertyLimitReached = false;

struct DeviceSet {
    HDEVINFO value;
    ~DeviceSet() { if (value != INVALID_HANDLE_VALUE) SetupDiDestroyDeviceInfoList(value); }
};
struct Property {
    ULONG type = 0;
    std::vector<BYTE> bytes;
};
Property registry(DEVINST node, ULONG key) {
    Property result;
    ULONG size = MaximumPropertyBytes;
    result.bytes.resize(size);
    auto status = CM_Get_DevNode_Registry_PropertyW(node, key, &result.type, result.bytes.data(), &size, 0);
    if (size > MaximumPropertyBytes) propertyLimitReached = true;
    if (status != CR_SUCCESS || size > MaximumPropertyBytes) return {};
    result.bytes.resize(size);
    return result;
}
Property property(DEVINST node, const DEVPROPKEY& key) {
    Property result;
    ULONG size = MaximumPropertyBytes;
    result.bytes.resize(size);
    auto status = CM_Get_DevNode_PropertyW(node, &key, &result.type, result.bytes.data(), &size, 0);
    if (size > MaximumPropertyBytes) propertyLimitReached = true;
    if (status != CR_SUCCESS || size > MaximumPropertyBytes) return {};
    result.bytes.resize(size);
    return result;
}
std::string quote(const std::wstring& text) {
    if (text.size() > 512) { propertyLimitReached = true; return "null"; }
    int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (bytes == 0 && !text.empty()) return "null";
    std::string utf8(bytes, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), utf8.data(), bytes, nullptr, nullptr);
    std::ostringstream output; output << '"';
    for (unsigned char c : utf8) {
        if (c == '"' || c == '\\') output << '\\' << c;
        else if (c < 32) output << "\\u" << std::hex << std::setw(4) << std::setfill('0') << unsigned(c);
        else output << c;
    }
    output << '"'; return output.str();
}
std::vector<std::wstring> strings(const Property& value, bool multiple) {
    if (value.type != (multiple ? REG_MULTI_SZ : REG_SZ) || value.bytes.empty() || value.bytes.size() % sizeof(wchar_t) != 0) return {};
    const auto* data = reinterpret_cast<const wchar_t*>(value.bytes.data());
    size_t count = value.bytes.size() / sizeof(wchar_t), offset = 0;
    std::vector<std::wstring> result;
    while (offset < count && data[offset] != 0 && result.size() < 16) {
        size_t end = offset;
        while (end < count && data[end] != 0) ++end;
        if (end == count || end - offset > 512) { propertyLimitReached = true; return {}; }
        result.emplace_back(data + offset, end - offset);
        if (!multiple) break;
        offset = end + 1;
    }
    if (multiple && result.size() == 16 && offset < count && data[offset] != 0) propertyLimitReached = true;
    return result;
}
std::string stringProperty(DEVINST node, ULONG key) {
    auto values = strings(registry(node, key), false);
    return values.empty() ? "null" : quote(values.front());
}
std::string array(const std::vector<std::wstring>& values) {
    std::ostringstream output; output << '[';
    for (size_t i = 0; i < values.size(); ++i) { if (i) output << ','; output << quote(values[i]); }
    output << ']'; return output.str();
}
std::optional<DWORD> integer(DEVINST node, ULONG key) {
    auto value = registry(node, key);
    DWORD number = 0;
    if (value.type != REG_DWORD || value.bytes.size() != sizeof(number)) return {};
    memcpy(&number, value.bytes.data(), sizeof(number));
    return number == MAXDWORD ? std::optional<DWORD>{} : number;
}
std::string number(std::optional<DWORD> value) { return value ? std::to_string(*value) : "null"; }
std::wstring instanceId(DEVINST node) {
    wchar_t id[MAX_DEVICE_ID_LEN] = {};
    return CM_Get_Device_IDW(node, id, MAX_DEVICE_ID_LEN, 0) == CR_SUCCESS ? id : L"";
}
bool pci(const std::wstring& id) { return id.rfind(L"PCI\\", 0) == 0; }
bool blackmagic(const std::vector<std::wstring>& ids) {
    for (auto id : ids) {
        std::transform(id.begin(), id.end(), id.begin(), [](wchar_t character) { return std::towupper(character); });
        const std::wstring prefix = L"PCI\\VEN_BDBD";
        if (id.rfind(prefix, 0) == 0 && (id.size() == prefix.size() || id[prefix.size()] == L'&')) return true;
    }
    return false;
}
std::string container(DEVINST node) {
    auto value = property(node, DEVPKEY_Device_ContainerId);
    if (value.type != DEVPROP_TYPE_GUID || value.bytes.size() != sizeof(GUID)) return "null";
    GUID id; memcpy(&id, value.bytes.data(), sizeof(id));
    if (IsEqualGUID(id, GUID_NULL)) return "null";
    wchar_t text[40] = {};
    return StringFromGUID2(id, text, 40) > 0 ? quote(text) : "null";
}
std::string physicalLocation(DEVINST node) {
    auto value = property(node, DEVPKEY_Device_PhysicalDeviceLocation);
    // ACPI _PLD is kept as raw evidence. Do not assign a chassis/slot meaning without parsing its version.
    if (value.type != DEVPROP_TYPE_BINARY || value.bytes.empty() || value.bytes.size() > 256) return "null";
    std::ostringstream output; output << '"';
    for (BYTE byte : value.bytes) output << std::hex << std::setw(2) << std::setfill('0') << unsigned(byte);
    output << '"'; return output.str();
}
std::string nodeJson(DEVINST node) {
    auto id = instanceId(node);
    auto address = integer(node, CM_DRP_ADDRESS);
    std::optional<DWORD> device, function;
    // Only the Windows PCI address property has a documented bit layout. The SDK's
    // TopologicalID remains opaque and is deliberately never decoded or joined here.
    if (pci(id) && address && (*address >> 16) <= 31 && (*address & 0xffff) <= 7) {
        device = *address >> 16; function = *address & 0xffff;
    }
    std::ostringstream output;
    output << "{\"instanceId\":" << (id.empty() ? "null" : quote(id))
        << ",\"description\":" << stringProperty(node, CM_DRP_DEVICEDESC)
        << ",\"friendlyName\":" << stringProperty(node, CM_DRP_FRIENDLYNAME)
        << ",\"manufacturer\":" << stringProperty(node, CM_DRP_MFG)
        << ",\"service\":" << stringProperty(node, CM_DRP_SERVICE)
        << ",\"hardwareIds\":" << array(strings(registry(node, CM_DRP_HARDWAREID), true))
        << ",\"busNumber\":" << number(integer(node, CM_DRP_BUSNUMBER))
        << ",\"address\":" << number(address)
        << ",\"deviceNumber\":" << number(device) << ",\"functionNumber\":" << number(function)
        << ",\"locationInfo\":" << stringProperty(node, CM_DRP_LOCATION_INFORMATION)
        << ",\"locationPaths\":" << array(strings(registry(node, CM_DRP_LOCATION_PATHS), true))
        << ",\"uiNumber\":" << number(integer(node, CM_DRP_UI_NUMBER))
        << ",\"containerId\":" << container(node)
        << ",\"physicalDeviceLocation\":" << physicalLocation(node) << '}';
    return output.str();
}
std::string deviceJson(DEVINST node) {
    auto own = nodeJson(node);
    own.pop_back();
    std::ostringstream output; output << own << ",\"parents\":[";
    DEVINST current = node, parent;
    std::vector<DEVINST> seen { node };
    size_t depth = 0;
    while (depth < MaximumParents && CM_Get_Parent(&parent, current, 0) == CR_SUCCESS) {
        if (std::find(seen.begin(), seen.end(), parent) != seen.end()) break;
        if (depth) output << ',';
        output << nodeJson(parent);
        seen.push_back(parent); current = parent; ++depth;
    }
    bool truncated = depth == MaximumParents && CM_Get_Parent(&parent, current, 0) == CR_SUCCESS;
    output << "],\"parentsTruncated\":" << (truncated ? "true" : "false") << '}';
    return output.str();
}
}

std::string pciInventoryJson() {
    propertyLimitReached = false;
    DeviceSet devices { SetupDiGetClassDevsW(nullptr, L"PCI", nullptr, DIGCF_PRESENT | DIGCF_ALLCLASSES) };
    if (devices.value == INVALID_HANDLE_VALUE) return "{\"available\":false,\"devices\":[],\"truncated\":false,\"error\":\"Windows PCI enumeration unavailable.\"}";
    std::vector<std::string> reports;
    size_t bytes = 0;
    bool truncated = false;
    for (DWORD index = 0; ; ++index) {
        if (index >= 4096) { truncated = true; break; }
        SP_DEVINFO_DATA device = {}; device.cbSize = sizeof(device);
        if (!SetupDiEnumDeviceInfo(devices.value, index, &device)) {
            if (GetLastError() != ERROR_NO_MORE_ITEMS) return "{\"available\":false,\"devices\":[],\"truncated\":false,\"error\":\"Windows PCI enumeration failed.\"}";
            break;
        }
        if (!blackmagic(strings(registry(device.DevInst, CM_DRP_HARDWAREID), true))) continue;
        auto report = deviceJson(device.DevInst);
        if (reports.size() >= MaximumNodes || bytes + report.size() > MaximumJsonBytes - 1024) { truncated = true; break; }
        bytes += report.size(); reports.push_back(report);
    }
    std::ostringstream output; output << "{\"available\":true,\"devices\":[";
    for (size_t i = 0; i < reports.size(); ++i) { if (i) output << ','; output << reports[i]; }
    output << "],\"truncated\":" << (truncated || propertyLimitReached ? "true" : "false") << ",\"error\":null}";
    return output.str();
}

PciInterfaceResolution blackmagicPciInstanceForInterface(const std::wstring& devicePath) {
    PciInterfaceResolution unavailable;
    unavailable.error = "DevicePath could not resolve to a registered interface; filter was not bound.";
    if (devicePath.empty() || devicePath.size() > 512) return unavailable;
    DeviceSet devices { SetupDiCreateDeviceInfoList(nullptr, nullptr) };
    if (devices.value == INVALID_HANDLE_VALUE) return unavailable;
    SP_DEVICE_INTERFACE_DATA interfaceData = {}; interfaceData.cbSize = sizeof(interfaceData);
    if (!SetupDiOpenDeviceInterfaceW(devices.value, devicePath.c_str(), 0, &interfaceData)) return unavailable;
    DWORD required = 0;
    SetupDiGetDeviceInterfaceDetailW(devices.value, &interfaceData, nullptr, 0, &required, nullptr);
    if (required < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || required > MaximumPropertyBytes) return unavailable;
    std::vector<BYTE> buffer(required);
    auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buffer.data()); detail->cbSize = sizeof(*detail);
    SP_DEVINFO_DATA info = {}; info.cbSize = sizeof(info);
    if (!SetupDiGetDeviceInterfaceDetailW(devices.value, &interfaceData, detail, required, nullptr, &info)) return unavailable;
    return resolveFirstPciAncestor(info.DevInst, [](DEVINST node) {
        auto id = instanceId(node);
        return PciNodeFacts { id, pci(id), pci(id) && blackmagic(strings(registry(node, CM_DRP_HARDWAREID), true)) };
    }, [](DEVINST node) -> std::optional<DEVINST> {
        DEVINST parent;
        return CM_Get_Parent(&parent, node, 0) == CR_SUCCESS ? std::optional<DEVINST>(parent) : std::nullopt;
    });
}
