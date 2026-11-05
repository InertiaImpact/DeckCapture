#include "PciAncestry.h"
#include <cassert>
#include <map>

int main() {
    std::map<int, PciNodeFacts> facts {
        {0, {L"DECKLINK\\AVSTREAM\\capture", false, false}}, {1, {L"DECKLINK\\BUS\\child", false, false}},
        {2, {L"PCI\\VEN_BDBD&DEV_A11B\\exact", true, true}}, {3, {L"PCI\\VEN_8086&DEV_AAAA\\other", true, false}}
    };
    std::map<int, int> parents { {0, 1}, {1, 2} };
    auto read = [&](int node) { return facts[node]; };
    auto parent = [&](int node) -> std::optional<int> { auto found = parents.find(node); return found == parents.end() ? std::nullopt : std::optional<int>(found->second); };
    auto result = resolveFirstPciAncestor(0, read, parent);
    assert(result.pciInstanceId == facts[2].instanceId && result.interfaceInstanceId == facts[0].instanceId && result.ancestorInstanceIds.size() == 2);
    assert(resolveFirstPciAncestor(2, read, parent).pciInstanceId == facts[2].instanceId);
    parents[1] = 3; parents[3] = 2;
    result = resolveFirstPciAncestor(0, read, parent);
    assert(result.pciInstanceId.empty() && result.ancestorInstanceIds.size() == 2); // Never pass a foreign PCI node.
    parents[1] = 0;
    result = resolveFirstPciAncestor(0, read, parent);
    assert(result.pciInstanceId.empty() && result.error.find("cycle") != std::string::npos);
    facts.clear(); parents.clear();
    for (int i = 0; i <= 9; ++i) { facts[i] = {L"DECKLINK\\node" + std::to_wstring(i), false, false}; parents[i] = i + 1; }
    result = resolveFirstPciAncestor(0, read, parent);
    assert(result.pciInstanceId.empty() && result.ancestryTruncated && result.ancestorInstanceIds.size() == 8);
    facts[8] = {L"PCI\\VEN_BDBD&DEV_A11B\\last-allowed", true, true};
    assert(!resolveFirstPciAncestor(0, read, parent).pciInstanceId.empty());
    parents.clear();
    assert(resolveFirstPciAncestor(0, read, parent).pciInstanceId.empty());
    facts[0] = {};
    assert(resolveFirstPciAncestor(0, read, parent).pciInstanceId.empty());
    return 0;
}
