#include <windows.h>
#include <werapi.h>
#include <filesystem>
#include <fstream>
#include <cstring>
#include "amd_context_compatibility.h"
#include "crash_monitor_client.h"

// CPU-only continuation probe. It never loads a Vulkan driver or starts the game.
int wmain(int argc, wchar_t** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    WerSetFlags(WER_FAULT_REPORTING_NO_UI);
    if (argc < 3) return 2;
    std::filesystem::path report(argv[2]);
    std::filesystem::create_directories(report);
    if (wcscmp(argv[1], L"worker") == 0) {
        DWORD flags = 0;
        if (!read_context_policy(GetCurrentProcess(), flags)) return 3;
        std::ofstream(report / L"policy.txt") << flags;
        if (argc >= 4 && wcscmp(argv[3], L"inspect") == 0) return 0;
        if (argc < 4) return 4;
        std::string status = start_crash_monitor(argv[3], report, report / L"bootstrap.jsonl");
        std::ofstream(report / L"monitor-status.txt") << status;
        if (status != "attached") return 5;
        if ((flags & context_validation_enabled) == 0) return 77;
        // Valid dynamic executable code, deliberately absent from any continuation table.
        // Strict policy rejects it; relaxed policy permits it while shadow stacks stay on.
        // RtlCaptureContext resumes at a caller-aligned RSP: allocate shadow space
        // and CALL ExitProcess so its entry has the Windows x64 alignment/return slot.
        BYTE code[] = {0x48, 0x83, 0xec, 0x20, 0xb9, 33, 0, 0, 0, 0x48, 0xb8,
            0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xd0, 0xcc};
        uintptr_t exit = reinterpret_cast<uintptr_t>(&ExitProcess);
        std::memcpy(code + 11, &exit, sizeof(exit));
        void* entry = VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!entry) return 6;
        std::memcpy(entry, code, sizeof(code));
        DWORD previous = 0;
        if (!VirtualProtect(entry, 4096, PAGE_EXECUTE_READ, &previous)) return 7;
        FlushInstructionCache(GetCurrentProcess(), entry, sizeof(code));
        CONTEXT context{};
        RtlCaptureContext(&context);
        context.Rip = reinterpret_cast<DWORD64>(entry);
        RtlRestoreContext(&context, nullptr);
        return 8;
    }
    if (argc < 4) return 9;
    std::wstring command = L"\"" + std::wstring(argv[0]) + L"\" worker \"" + report.wstring() + L"\" \"" + argv[3] + L"\"";
    PROCESS_INFORMATION child{};
    bool created = false;
    if (wcscmp(argv[1], L"relaxed") == 0) {
        created = start_with_relaxed_continuations(argv[0], command, child);
    } else {
        SIZE_T size = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        auto storage = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, size));
        auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage);
        if (!storage || !InitializeProcThreadAttributeList(attributes, 1, 0, &size)) return 10;
        DWORD64 policy[2] = {0, PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_ON |
            PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_ON};
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.lpAttributeList = attributes;
        if (UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                policy, sizeof(policy), nullptr, nullptr))
            created = CreateProcessW(argv[0], command.data(), nullptr, nullptr, FALSE,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, nullptr, &startup.StartupInfo, &child) != FALSE;
        DeleteProcThreadAttributeList(attributes);
        HeapFree(GetProcessHeap(), 0, storage);
    }
    if (!created) { std::ofstream(report / L"launch-error.txt") << GetLastError(); return 11; }
    CloseHandle(child.hThread);
    if (WaitForSingleObject(child.hProcess, 20000) != WAIT_OBJECT_0) return 12;
    DWORD result = 0;
    GetExitCodeProcess(child.hProcess, &result);
    CloseHandle(child.hProcess);
    std::ofstream(report / L"exit.txt") << result;
    return 0;
}
