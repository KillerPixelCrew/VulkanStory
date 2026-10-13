#include <windows.h>
#include <fstream>
#include "crash_monitor_client.h"

std::string start_crash_monitor(const std::filesystem::path& executable,
    const std::filesystem::path& report, const std::filesystem::path& bootstrap) {
    if (IsDebuggerPresent()) return "skipped: another debugger is attached";
    if (!std::filesystem::is_regular_file(executable)) return "unavailable: crash monitor is missing";
    std::filesystem::create_directories(report);
    auto bootstrap_utf8 = bootstrap.u8string();
    std::ofstream(report / L"bootstrap-path.txt") << std::string(bootstrap_utf8.begin(), bootstrap_utf8.end());
    std::wstring ready_name = L"Local\\VulkanStory.CrashReady." + std::to_wstring(GetCurrentProcessId()) +
        L"." + std::to_wstring(GetTickCount64());
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, ready_name.c_str());
    if (!ready) return "unavailable: CreateEvent " + std::to_string(GetLastError());
    std::wstring command = L"\"" + executable.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId()) +
        L" \"" + report.wstring() + L"\" \"" + ready_name + L"\"";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, executable.parent_path().c_str(), &startup, &child)) {
        DWORD error = GetLastError();
        CloseHandle(ready);
        return "unavailable: CreateProcess " + std::to_string(error);
    }
    HANDLE waits[] = {ready, child.hProcess};
    DWORD result = WaitForMultipleObjects(2, waits, FALSE, 10000);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    CloseHandle(ready);
    if (result != WAIT_OBJECT_0) return "unavailable: crash monitor did not become ready";
    if (!std::filesystem::is_regular_file(report / L"monitor-ready.txt"))
        return "unavailable: crash monitor could not attach (see monitor.txt)";
    return "attached";
}
