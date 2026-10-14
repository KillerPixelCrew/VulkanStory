#include "amd_context_compatibility.h"
#include <vector>

bool read_context_policy(HANDLE process, DWORD& flags) {
    flags = 0;
    return GetProcessMitigationPolicy(process, ProcessUserShadowStackPolicy, &flags, sizeof(flags)) != FALSE;
}

bool primary_display_is_amd() {
    DISPLAY_DEVICEW display{};
    display.cb = sizeof(display);
    for (DWORD index = 0; EnumDisplayDevicesW(nullptr, index, &display, 0); ++index) {
        if ((display.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
            return std::wstring(display.DeviceID).find(L"VEN_1002") != std::wstring::npos;
        display = {};
        display.cb = sizeof(display);
    }
    return false;
}

bool start_with_relaxed_continuations(const wchar_t* executable, std::wstring command,
    PROCESS_INFORMATION& child) {
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    if (!size) return false;
    std::vector<BYTE> storage(size);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &size)) return false;
    DWORD64 policies[2] = {0, PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_RELAXED_MODE};
    BOOL updated = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
        policies, sizeof(policies), nullptr, nullptr);
    DWORD error = updated ? ERROR_SUCCESS : GetLastError();
    BOOL created = FALSE;
    if (updated) {
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.lpAttributeList = attributes;
        created = CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, nullptr, &startup.StartupInfo, &child);
        if (!created) error = GetLastError();
    }
    DeleteProcThreadAttributeList(attributes);
    SetLastError(error);
    return created != FALSE;
}
