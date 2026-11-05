#pragma once
#include <string>

// Explicit idle-only mode. Filter binding can access hardware; normal discovery never calls this.
std::string directShowCorrelationJson();
