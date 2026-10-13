#pragma once
#include <windows.h>
#include <string>

bool read_context_policy(HANDLE process, DWORD& flags);
bool primary_display_is_amd();
// Only continuation validation changes. All other process mitigations defer to their existing settings.
bool start_with_relaxed_continuations(const wchar_t* executable, std::wstring command,
    PROCESS_INFORMATION& child);

constexpr DWORD context_validation_enabled = 1u << 2;
constexpr DWORD context_validation_relaxed = 1u << 9;
constexpr DWORD user_shadow_stack_enabled = 1u;
