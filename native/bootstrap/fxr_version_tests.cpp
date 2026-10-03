#include "fxr_version.h"
#include <iostream>

int main() {
    using vulkanstory::fxr_version;
    int failures = 0;
    auto expect = [&](bool condition, const char* name) { if (!condition) { std::cerr << name << '\n'; ++failures; } };
    auto less = [](const wchar_t* a, const wchar_t* b) {
        auto left = fxr_version::parse(a), right = fxr_version::parse(b);
        return left && right && *left < *right;
    };
    expect(less(L"9.0.20", L"10.0.12"), "Select major numerically");
    expect(less(L"10.0.9", L"10.0.12"), "Select patch numerically");
    expect(less(L"10.0.0-rc.2", L"10.0.0-rc.10"), "Select prerelease numeric identifiers numerically");
    expect(less(L"10.0.0-rc.10", L"10.0.0"), "Release wins over same-version prerelease");
    expect(less(L"10.0.12", L"11.0.0-preview.1"), "Match hostfxr selection across release families");
    expect(!less(L"10.0.12+abc", L"10.0.12+def"), "Build metadata does not change precedence");
    for (const auto* invalid : {L"latest", L"10.0", L"10.0.1.2", L"10.0.0-", L"10.0.0-rc..1", L"10.0.00", L"10.0.0-rc.01"})
        expect(!fxr_version::parse(invalid), "Reject invalid host directory version");
    return failures == 0 ? 0 : 1;
}
