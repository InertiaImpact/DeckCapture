#pragma once
#include <algorithm>
#include <optional>
#include <string>
#include <vector>

struct PciInterfaceResolution {
    std::wstring interfaceInstanceId;
    std::wstring pciInstanceId;
    std::vector<std::wstring> ancestorInstanceIds;
    bool ancestryTruncated = false;
    std::string error;
};
struct PciNodeFacts { std::wstring instanceId; bool isPci = false; bool isBlackmagic = false; };

// The callbacks are read-only CM queries in production and finite tree fixtures in tests.
// A PCI ancestor is a stopping boundary: never walk past a non-Blackmagic PCI device.
template<class Node, class ReadFacts, class ReadParent>
PciInterfaceResolution resolveFirstPciAncestor(Node start, ReadFacts read, ReadParent parentOf) {
    PciInterfaceResolution result;
    std::vector<Node> seen;
    Node current = start;
    for (size_t depth = 0; depth <= 8; ++depth) {
        if (std::find(seen.begin(), seen.end(), current) != seen.end()) {
            result.error = "Interface parent chain contains a cycle; filter was not bound."; return result;
        }
        seen.push_back(current);
        auto facts = read(current);
        if (facts.instanceId.empty()) { result.error = "Interface parent identity was unavailable; filter was not bound."; return result; }
        if (depth == 0) result.interfaceInstanceId = facts.instanceId;
        else result.ancestorInstanceIds.push_back(facts.instanceId);
        if (facts.isPci) {
            if (facts.isBlackmagic) result.pciInstanceId = facts.instanceId;
            else result.error = "The first PCI ancestor is not Blackmagic; filter was not bound.";
            return result;
        }
        auto parent = parentOf(current);
        if (!parent) { result.error = "Interface has no PCI ancestor; filter was not bound."; return result; }
        if (depth == 8) {
            result.ancestryTruncated = true; result.error = "Interface parent chain exceeded eight ancestors; filter was not bound."; return result;
        }
        current = *parent;
    }
    return result;
}
