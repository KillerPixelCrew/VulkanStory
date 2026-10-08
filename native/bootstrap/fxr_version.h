#pragma once
#include <array>
#include <cstdint>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace vulkanstory {
/// @brief Parsed host/fxr directory version used to select an installed hostfxr.
/// @details Stores three numeric core components and prerelease identifiers. Nonempty build metadata is discarded for precedence.
struct fxr_version {
    /// @brief Major, minor and patch numbers used in numeric precedence.
    std::array<std::uint64_t, 3> numbers{};
    /// @brief Owned prerelease identifiers; an empty vector denotes a release version.
    std::vector<std::wstring> prerelease;

    /// @brief Checks for a nonempty sequence of ASCII decimal digits.
    static bool digits(std::wstring_view value) {
        if (value.empty()) return false;
        for (wchar_t c : value) if (c < L'0' || c > L'9') return false;
        return true;
    }

    /// @brief Parses three numeric core components and optional prerelease identifiers.
    /// @details Rejects core overflow, missing components, leading zeroes and malformed prerelease identifiers; strips nonempty build metadata.
    /// @param value Borrowed directory-name text valid for the call.
    /// @return Owned parsed value, or no value when validation fails.
    static std::optional<fxr_version> parse(std::wstring_view value) {
        fxr_version result;
        // .NET host/fxr directories use SemVer; build metadata does not affect precedence.
        const auto plus = value.find(L'+');
        if (plus != value.npos) {
            if (plus + 1 == value.size()) return {};
            value = value.substr(0, plus);
        }
        auto dash = value.find(L'-');
        auto base = value.substr(0, dash);
        for (std::size_t i = 0; i != result.numbers.size(); ++i) {
            auto dot = base.find(L'.');
            if ((i < 2 && dot == base.npos) || (i == 2 && dot != base.npos)) return {};
            auto part = base.substr(0, dot);
            if (!digits(part) || (part.size() > 1 && part.front() == L'0')) return {};
            std::uint64_t n = 0;
            for (wchar_t c : part) {
                auto digit = static_cast<std::uint64_t>(c - L'0');
                if (n > (std::numeric_limits<std::uint64_t>::max() - digit) / 10) return {};
                n = n * 10 + digit;
            }
            result.numbers[i] = n;
            if (dot != base.npos) base.remove_prefix(dot + 1);
        }
        if (dash != value.npos) {
            auto pre = value.substr(dash + 1);
            do {
                auto dot = pre.find(L'.');
                auto part = pre.substr(0, dot);
                if (part.empty()) return {};
                for (wchar_t c : part)
                    if (!((c >= L'0' && c <= L'9') || (c >= L'A' && c <= L'Z') ||
                          (c >= L'a' && c <= L'z') || c == L'-')) return {};
                if (digits(part) && part.size() > 1 && part.front() == L'0') return {};
                result.prerelease.emplace_back(part);
                if (dot == pre.npos) break;
                pre.remove_prefix(dot + 1);
            } while (true);
        }
        return result;
    }

    /// @brief Compares core numbers followed by SemVer prerelease precedence.
    /// @details Numeric prerelease identifiers precede text identifiers; a release wins over its same-core prerelease. Build metadata does not affect this comparison.
    bool operator<(const fxr_version& rhs) const {
        if (numbers != rhs.numbers) return numbers < rhs.numbers;
        if (prerelease.empty() || rhs.prerelease.empty())
            return !prerelease.empty() && rhs.prerelease.empty();
        for (std::size_t i = 0; i < prerelease.size() && i < rhs.prerelease.size(); ++i) {
            const auto& a = prerelease[i];
            const auto& b = rhs.prerelease[i];
            if (a == b) continue;
            const bool an = digits(a), bn = digits(b);
            if (an != bn) return an;
            if (an && a.size() != b.size()) return a.size() < b.size();
            return a < b;
        }
        return prerelease.size() < rhs.prerelease.size();
    }
};
}
