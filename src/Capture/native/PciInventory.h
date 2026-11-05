#pragma once
#include <string>
#include "PciAncestry.h"

// Raw Windows evidence only. This function does not correlate SDK endpoints or infer card membership.
std::string pciInventoryJson();
// Exact registered interface -> its first PCI ancestor. No matching by names/opaque IDs.
PciInterfaceResolution blackmagicPciInstanceForInterface(const std::wstring& devicePath);
