#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <winver.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <map>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {
std::string utf8(const std::wstring& text) {
    if (text.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string value(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), value.data(), count, nullptr, nullptr);
    return value;
}

void copy_file(const fs::path& from, const fs::path& to, std::ostream& log) {
    try {
        if (!fs::is_regular_file(from)) return;
        fs::create_directories(to.parent_path());
        fs::copy_file(from, to, fs::copy_options::overwrite_existing);
    } catch (const std::exception& error) { log << "Copy failed: " << error.what() << '\n'; }
}

fs::path read_path(const fs::path& file) {
    std::ifstream input(file);
    std::string value;
    std::getline(input, value);
    if (value.starts_with("\xef\xbb\xbf")) value.erase(0, 3);
    return fs::u8path(value);
}

void collect_logs(const fs::path& report, std::ostream& log) {
    copy_file(read_path(report / L"bootstrap-path.txt"), report / L"bootstrap.jsonl", log);
    fs::path data = read_path(report / L"game-data-path.txt");
    if (data.empty() || !fs::is_directory(data / L"Logs")) return;
    size_t copied = 0;
    for (const auto& file : fs::recursive_directory_iterator(data / L"Logs", fs::directory_options::skip_permission_denied)) {
        if (!file.is_regular_file()) continue;
        if (++copied > 256 || file.file_size() > 16 * 1024 * 1024) {
            log << "Log omitted by size/count limit: " << utf8(file.path().filename().wstring()) << '\n';
            continue;
        }
        copy_file(file.path(), report / L"game-logs" / fs::relative(file.path(), data / L"Logs"), log);
    }
}

void write_modules(DWORD pid, std::ostream& output) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) { output << "Module snapshot failed: " << GetLastError() << '\n'; return; }
    MODULEENTRY32W module{sizeof(module)};
    if (Module32FirstW(snapshot, &module)) do {
        output << "0x" << std::hex << reinterpret_cast<uintptr_t>(module.modBaseAddr) << " size=0x"
            << module.modBaseSize << std::dec << ' ' << utf8(module.szExePath);
        DWORD ignored = 0;
        DWORD bytes = GetFileVersionInfoSizeW(module.szExePath, &ignored);
        if (bytes) {
            std::vector<BYTE> version(bytes);
            VS_FIXEDFILEINFO* info = nullptr;
            UINT size = 0;
            if (GetFileVersionInfoW(module.szExePath, 0, bytes, version.data()) &&
                    VerQueryValueW(version.data(), L"\\", reinterpret_cast<void**>(&info), &size) && size >= sizeof(*info))
                output << " version=" << HIWORD(info->dwFileVersionMS) << '.' << LOWORD(info->dwFileVersionMS)
                    << '.' << HIWORD(info->dwFileVersionLS) << '.' << LOWORD(info->dwFileVersionLS);
        }
        output << '\n';
    } while (Module32NextW(snapshot, &module));
    CloseHandle(snapshot);
}

void write_stack(HANDLE process, HANDLE thread, CONTEXT context, std::ostream& output) {
    STACKFRAME64 frame{};
    frame.AddrPC.Offset = context.Rip;
    frame.AddrStack.Offset = context.Rsp;
    frame.AddrFrame.Offset = context.Rbp;
    frame.AddrPC.Mode = frame.AddrStack.Mode = frame.AddrFrame.Mode = AddrModeFlat;
    DWORD64 previous = 0, previous_stack = 0;
    for (unsigned index = 0; index < 96 && frame.AddrPC.Offset; ++index) {
        bool walked = StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, thread, &frame, &context, nullptr,
            SymFunctionTableAccess64, SymGetModuleBase64, nullptr) != FALSE;
        DWORD64 address = frame.AddrPC.Offset;
        if (!address || (address == previous && frame.AddrStack.Offset == previous_stack)) break;
        previous = address;
        previous_stack = frame.AddrStack.Offset;
        IMAGEHLP_MODULE64 module{sizeof(module)};
        output << "  #" << index << " 0x" << std::hex << address;
        if (SymGetModuleInfo64(process, address, &module))
            output << ' ' << module.ModuleName << "+0x" << (address - module.BaseOfImage);
        alignas(SYMBOL_INFO) BYTE storage[sizeof(SYMBOL_INFO) + MAX_SYM_NAME]{};
        auto symbol = reinterpret_cast<SYMBOL_INFO*>(storage);
        symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
        symbol->MaxNameLen = MAX_SYM_NAME;
        DWORD64 displacement = 0;
        if (SymFromAddr(process, address, &displacement, symbol)) output << ' ' << symbol->Name << "+0x" << displacement;
        output << std::dec << '\n';
        if (!walked) break;
    }
}

void capture(HANDLE process, DWORD pid, DWORD crashing_thread, const EXCEPTION_DEBUG_INFO& exception,
    const std::map<DWORD, HANDLE>& threads, const fs::path& report) {
    std::ofstream output(report / L"crash.txt");
    const auto& record = exception.ExceptionRecord;
    output << "VulkanStory native crash\nPID=" << pid << " thread=" << crashing_thread << "\nException=0x"
        << std::hex << record.ExceptionCode << " flags=0x" << record.ExceptionFlags << " address=0x"
        << reinterpret_cast<uintptr_t>(record.ExceptionAddress) << std::dec << '\n';
    for (DWORD i = 0; i < record.NumberParameters && i < EXCEPTION_MAXIMUM_PARAMETERS; ++i)
        output << "Parameter[" << i << "]=0x" << std::hex << record.ExceptionInformation[i] << std::dec << '\n';

    CONTEXT fatal{};
    fatal.ContextFlags = CONTEXT_ALL;
    auto crashed = threads.find(crashing_thread);
    bool have_context = crashed != threads.end() && GetThreadContext(crashed->second, &fatal);
    if (!have_context) output << "Crashing thread context unavailable: " << GetLastError() << '\n';
    EXCEPTION_RECORD local_record = record;
    EXCEPTION_POINTERS pointers{&local_record, &fatal};
    MINIDUMP_EXCEPTION_INFORMATION details{crashing_thread, &pointers, FALSE};
    HANDLE dump = CreateFileW((report / L"crash.dmp").c_str(), GENERIC_WRITE, FILE_SHARE_READ,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (dump != INVALID_HANDLE_VALUE) {
        // Keep native thread contexts/stacks and module identities; do not copy the game heap.
        auto type = static_cast<MINIDUMP_TYPE>(MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules |
            MiniDumpWithModuleHeaders | MiniDumpIgnoreInaccessibleMemory | MiniDumpFilterMemory);
        BOOL written = MiniDumpWriteDump(process, pid, dump, type, have_context ? &details : nullptr, nullptr, nullptr);
        output << "Minidump=" << (written ? "written" : "failed") << " error=" << (written ? 0 : GetLastError()) << '\n';
        CloseHandle(dump);
    } else output << "Minidump file unavailable: " << GetLastError() << '\n';

    SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
    // An explicit local path prevents inherited symbol-server settings from causing network waits.
    bool symbols = SymInitializeW(process, report.c_str(), TRUE) != FALSE;
    output << "\nThread stacks (native modules and offsets):\n";
    for (const auto& [id, handle] : threads) {
        CONTEXT context{};
        context.ContextFlags = CONTEXT_ALL;
        output << "Thread " << id << (id == crashing_thread ? " [CRASHED]" : "") << '\n';
        if (GetThreadContext(handle, &context)) {
            output << "RIP=0x" << std::hex << context.Rip << " RSP=0x" << context.Rsp << " RBP=0x"
                << context.Rbp << std::dec << '\n';
            if (symbols) write_stack(process, handle, context, output);
        } else output << "Context unavailable: " << GetLastError() << '\n';
    }
    if (symbols) SymCleanup(process);
    output << "\nLoaded modules (including driver versions):\n";
    write_modules(pid, output);
    output.flush();
    collect_logs(report, output);
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 4) return 2;
    DWORD pid = wcstoul(argv[1], nullptr, 10);
    fs::path report(argv[2]);
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[3]);
    std::ofstream log(report / L"monitor.txt", std::ios::app);
    if (!DebugActiveProcess(pid)) {
        log << "Attach failed: " << GetLastError() << '\n';
        if (ready) { SetEvent(ready); CloseHandle(ready); }
        return 3;
    }
    // If this helper is closed or crashes, detach; never terminate the game with it.
    if (!DebugSetProcessKillOnExit(FALSE)) {
        log << "Disable kill-on-exit failed: " << GetLastError() << '\n';
        DebugActiveProcessStop(pid);
        if (ready) { SetEvent(ready); CloseHandle(ready); }
        return 4;
    }
    HANDLE process = nullptr;
    std::map<DWORD, HANDLE> threads;
    bool initial_breakpoint = true;
    bool captured = false;
    DEBUG_EVENT event{};
    while (WaitForDebugEventEx(&event, INFINITE)) {
        DWORD disposition = DBG_CONTINUE;
        bool done = false;
        switch (event.dwDebugEventCode) {
        case CREATE_PROCESS_DEBUG_EVENT:
            process = event.u.CreateProcessInfo.hProcess;
            threads[event.dwThreadId] = event.u.CreateProcessInfo.hThread;
            if (event.u.CreateProcessInfo.hFile) CloseHandle(event.u.CreateProcessInfo.hFile);
            break;
        case CREATE_THREAD_DEBUG_EVENT: threads[event.dwThreadId] = event.u.CreateThread.hThread; break;
        case EXIT_THREAD_DEBUG_EVENT: threads.erase(event.dwThreadId); break;
        case LOAD_DLL_DEBUG_EVENT: if (event.u.LoadDll.hFile) CloseHandle(event.u.LoadDll.hFile); break;
        case EXCEPTION_DEBUG_EVENT:
            if (initial_breakpoint && event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT) {
                initial_breakpoint = false;
                std::ofstream(report / L"monitor-ready.txt") << "attached";
                if (ready) { SetEvent(ready); CloseHandle(ready); ready = nullptr; }
            } else {
                disposition = DBG_EXCEPTION_NOT_HANDLED;
                if (!captured && (!event.u.Exception.dwFirstChance ||
                        event.u.Exception.ExceptionRecord.ExceptionCode == 0xc0000409)) {
                    captured = true;
                    try { capture(process, pid, event.dwThreadId, event.u.Exception, threads, report); }
                    catch (const std::exception& error) { log << "Capture failed: " << error.what() << '\n'; }
                }
            }
            break;
        case EXIT_PROCESS_DEBUG_EVENT:
            log << "Exit code=0x" << std::hex << event.u.ExitProcess.dwExitCode << std::dec << '\n';
            done = true;
            break;
        }
        if (!ContinueDebugEvent(event.dwProcessId, event.dwThreadId, disposition) || done) break;
    }
    if (ready) { SetEvent(ready); CloseHandle(ready); }
    DebugActiveProcessStop(pid);
    if (captured || fs::is_regular_file(report / L"managed-crash.txt")) {
        wchar_t system[MAX_PATH]{};
        GetSystemDirectoryW(system, MAX_PATH);
        fs::path powershell = fs::path(system) / L"WindowsPowerShell/v1.0/powershell.exe";
        std::wstring command = L"\"" + powershell.wstring() + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" +
            (fs::path(argv[0]).parent_path() / L"VulkanStory.CrashReport.ps1").wstring() +
            L"\" -ReportDirectory \"" + report.wstring() + L"\"";
        STARTUPINFOW startup{sizeof(startup)};
        PROCESS_INFORMATION collector{};
        if (CreateProcessW(powershell.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                nullptr, report.c_str(), &startup, &collector)) {
            CloseHandle(collector.hThread);
            CloseHandle(collector.hProcess);
        } else log << "Report packaging failed: " << GetLastError() << '\n';
    }
    return 0;
}
