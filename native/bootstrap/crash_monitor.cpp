#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <winver.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
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
    return fs::path(std::u8string(value.begin(), value.end()));
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

bool write_modules(DWORD pid, std::ostream& output) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) { output << "Module snapshot failed: " << GetLastError() << '\n'; return false; }
    MODULEENTRY32W module{};
    module.dwSize = sizeof(module);
    if (!Module32FirstW(snapshot, &module)) {
        output << "First module unavailable: " << GetLastError() << '\n';
        CloseHandle(snapshot);
        return false;
    }
    do {
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
    const DWORD error = GetLastError();
    CloseHandle(snapshot);
    if (error != ERROR_NO_MORE_FILES) output << "Module enumeration failed: " << error << '\n';
    return error == ERROR_NO_MORE_FILES;
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
        IMAGEHLP_MODULE64 module{};
        module.SizeOfStruct = sizeof(module);
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

struct RegisterValue { const char* name; DWORD64 value; };

std::array<RegisterValue, 17> register_values(const CONTEXT& context) {
    return {{{"RAX", context.Rax}, {"RBX", context.Rbx}, {"RCX", context.Rcx}, {"RDX", context.Rdx},
        {"RSI", context.Rsi}, {"RDI", context.Rdi}, {"RBP", context.Rbp}, {"RSP", context.Rsp},
        {"R8", context.R8}, {"R9", context.R9}, {"R10", context.R10}, {"R11", context.R11},
        {"R12", context.R12}, {"R13", context.R13}, {"R14", context.R14}, {"R15", context.R15},
        {"RIP", context.Rip}}};
}

void write_registers(const CONTEXT& context, std::ostream& output) {
    for (const auto& value : register_values(context))
        output << value.name << "=0x" << std::hex << value.value << ' ';
    output << "EFLAGS=0x" << context.EFlags << std::dec << '\n';
}

// This is an explicit teardown diagnostic, separate from the ordinary minidump.
// Snapshot nearby data objects without vendor-specific offsets or an entire heap.
void capture_fault_memory(HANDLE process, const CONTEXT& context, const EXCEPTION_RECORD& exception,
    const fs::path& report, std::ostream& log) {
    constexpr size_t byte_limit = 2 * 1024 * 1024;
    constexpr size_t root_limit = 1024 * 1024;
    constexpr size_t first_level_limit = 1792 * 1024;
    constexpr size_t object_window = 192 * 1024;
    constexpr size_t followed_window = 16 * 1024;
    constexpr size_t page_limit = 512;
    constexpr size_t query_limit = 16384;
    constexpr size_t target_limit = 512;
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    const uintptr_t page_size = system.dwPageSize;
    const uintptr_t minimum = reinterpret_cast<uintptr_t>(system.lpMinimumApplicationAddress);
    const uintptr_t maximum = reinterpret_cast<uintptr_t>(system.lpMaximumApplicationAddress);
    auto page_base = [page_size](uintptr_t address) { return address - address % page_size; };
    auto readable = [](DWORD protection) {
        if (protection & (PAGE_GUARD | PAGE_NOACCESS)) return false;
        switch (protection & 0xff) {
        case PAGE_READONLY: case PAGE_READWRITE: case PAGE_WRITECOPY:
        case PAGE_EXECUTE_READ: case PAGE_EXECUTE_READWRITE: case PAGE_EXECUTE_WRITECOPY: return true;
        default: return false;
        }
    };
    auto executable = [](DWORD protection) {
        const DWORD base = protection & 0xff;
        return base == PAGE_EXECUTE || base == PAGE_EXECUTE_READ ||
            base == PAGE_EXECUTE_READWRITE || base == PAGE_EXECUTE_WRITECOPY;
    };
    MEMORY_BASIC_INFORMATION stack{};
    const bool have_stack = VirtualQueryEx(process, reinterpret_cast<const void*>(context.Rsp),
        &stack, sizeof(stack)) == sizeof(stack);
    auto on_stack = [&stack, have_stack](uintptr_t address) {
        const uintptr_t start = reinterpret_cast<uintptr_t>(stack.BaseAddress);
        return have_stack && address >= start && address - start < stack.RegionSize;
    };
    std::ofstream manifest(report / L"fault-memory.txt");
    std::ofstream bytes(report / L"fault-memory.bin", std::ios::binary);
    if (!manifest || !bytes) throw std::runtime_error("Fault-memory output unavailable");
    manifest << "VulkanStory bounded native fault memory\nbyteLimit=" << byte_limit
        << " pageLimit=" << page_limit << " pointerQueryLimit=" << query_limit
        << " pointerTargetLimit=" << target_limit << " maximumPointerDepth=2\nrootByteLimit=" << root_limit
        << " firstLevelByteLimit=" << first_level_limit << " rootObjectWindow=" << object_window
        << " followedObjectWindow=" << followed_window
        << "\nThis is a partial snapshot; omitted memory does not imply an invalid pointer.\n";
    write_registers(context, manifest);
    manifest << "Page records: address size fileOffset protection memoryType depth origin\n";
    struct Page { uintptr_t address; std::vector<BYTE> bytes; unsigned depth; bool follow; };
    std::vector<Page> pages;
    std::map<uintptr_t, size_t> page_indices;
    size_t total_bytes = 0;
    auto capture_page = [&](uintptr_t address, unsigned depth, const std::string& origin,
        bool allow_code, size_t ceiling) {
        if (address < minimum || address > maximum) return false;
        const uintptr_t base = page_base(address);
        auto previous = page_indices.find(base);
        if (previous != page_indices.end()) {
            pages[previous->second].depth = (std::min)(pages[previous->second].depth, depth);
            manifest << "Reference 0x" << std::hex << address << " page=0x" << base << std::dec
                << " depth=" << depth << ' ' << origin << '\n';
            return true;
        }
        if (pages.size() >= page_limit || total_bytes > ceiling || page_size > ceiling - total_bytes)
            return false;
        MEMORY_BASIC_INFORMATION memory{};
        if (VirtualQueryEx(process, reinterpret_cast<const void*>(base), &memory, sizeof(memory)) != sizeof(memory) ||
            memory.State != MEM_COMMIT || !readable(memory.Protect) || (!allow_code && executable(memory.Protect)))
            return false;
        std::vector<BYTE> buffer(page_size);
        SIZE_T received = 0;
        ReadProcessMemory(process, reinterpret_cast<const void*>(base), buffer.data(), buffer.size(), &received);
        if (!received) return false;
        buffer.resize(received);
        bytes.write(reinterpret_cast<const char*>(buffer.data()), static_cast<std::streamsize>(buffer.size()));
        if (!bytes) throw std::runtime_error("Fault-memory write failed");
        manifest << "Page 0x" << std::hex << base << " 0x" << received << " 0x" << total_bytes
            << " 0x" << memory.Protect << " 0x" << memory.Type << std::dec << ' ' << depth << ' ' << origin << '\n';
        page_indices.emplace(base, pages.size());
        pages.push_back({base, std::move(buffer), depth, !executable(memory.Protect) &&
            (memory.Type == MEM_PRIVATE || memory.Type == MEM_MAPPED) && !on_stack(base)});
        total_bytes += received;
        return true;
    };
    auto capture_window = [&](uintptr_t address, size_t size, unsigned depth, const std::string& origin,
        bool allow_code, size_t ceiling) {
        if (!capture_page(address, depth, origin, allow_code, ceiling)) return false;
        uintptr_t next = page_base(address);
        for (size_t offset = page_size; offset < size && next <= maximum - page_size; offset += page_size) {
            next += page_size;
            if (!capture_page(next, depth, origin, allow_code, ceiling)) break;
        }
        return true;
    };
    const uintptr_t instruction = reinterpret_cast<uintptr_t>(exception.ExceptionAddress);
    for (const uintptr_t address : {static_cast<uintptr_t>(context.Rip), instruction}) {
        capture_window(address, 2 * page_size, 0, "instruction", true, byte_limit);
        if (address >= minimum + page_size) capture_page(address - page_size, 0, "instruction-before", true, byte_limit);
    }
    if (exception.ExceptionCode == EXCEPTION_ACCESS_VIOLATION && exception.NumberParameters >= 2) {
        const uintptr_t fault = static_cast<uintptr_t>(exception.ExceptionInformation[1]);
        manifest << "FaultAddress 0x" << std::hex << fault << std::dec << '\n';
        if (!capture_page(fault, 0, "fault-address", true, byte_limit)) manifest << "FaultAddress unreadable or omitted by limits\n";
    }
    // Preserve one page for every readable register before spending the root budget
    // on larger forward windows. Stack/code bytes are already in the minidump.
    for (const auto& value : register_values(context)) {
        manifest << "Root " << value.name << " 0x" << std::hex << value.value << std::dec << '\n';
        capture_page(static_cast<uintptr_t>(value.value), 0, value.name, false, root_limit);
    }
    for (const auto& value : register_values(context))
        if (!on_stack(static_cast<uintptr_t>(value.value)))
            capture_window(static_cast<uintptr_t>(value.value), object_window, 0, value.name, false, root_limit);

    size_t queries = 0, targets = 0;
    std::set<uintptr_t> followed;
    for (unsigned depth = 1; depth <= 2; ++depth) {
        struct Candidate { uintptr_t target; uintptr_t source; };
        std::vector<Candidate> candidates;
        std::set<uintptr_t> discovered;
        const size_t query_ceiling = depth * query_limit / 2;
        // Discover candidates before appending pages, so references remain stable.
        for (const auto& page : pages) {
            if (!page.follow || page.depth != depth - 1) continue;
            for (size_t offset = 0; offset + sizeof(uintptr_t) <= page.bytes.size(); offset += sizeof(uintptr_t)) {
                uintptr_t pointer = 0;
                std::memcpy(&pointer, page.bytes.data() + offset, sizeof(pointer));
                if (pointer < minimum || pointer > maximum || pointer % alignof(uintptr_t) ||
                    on_stack(pointer) || page_indices.contains(page_base(pointer)) ||
                    followed.contains(pointer) || discovered.contains(pointer)) continue;
                if (queries >= query_ceiling || candidates.size() >= target_limit / 2) break;
                ++queries;
                MEMORY_BASIC_INFORMATION memory{};
                if (VirtualQueryEx(process, reinterpret_cast<const void*>(pointer), &memory, sizeof(memory)) != sizeof(memory) ||
                    memory.State != MEM_COMMIT || !readable(memory.Protect) || executable(memory.Protect) ||
                    (memory.Type != MEM_PRIVATE && memory.Type != MEM_MAPPED)) continue;
                discovered.insert(pointer);
                candidates.push_back({pointer, page.address + offset});
            }
            if (queries >= query_ceiling || candidates.size() >= target_limit / 2) break;
        }
        const size_t ceiling = depth == 1 ? first_level_limit : byte_limit;
        for (const auto& candidate : candidates) {
            std::ostringstream origin;
            origin << "pointer-at-0x" << std::hex << candidate.source << "-to-0x" << candidate.target;
            if (capture_window(candidate.target, followed_window, depth, origin.str(), false, ceiling)) {
                followed.insert(candidate.target);
                ++targets;
            }
            if (pages.size() >= page_limit || total_bytes + page_size > ceiling) break;
        }
    }
    manifest << "Summary bytes=" << total_bytes << " pages=" << pages.size() << " pointerQueries=" << queries
        << " pointerTargets=" << targets << " boundedSnapshot=1\n";
    manifest.flush();
    bytes.flush();
    log << "Fault-memory snapshot bytes=" << total_bytes << " pages=" << pages.size() << '\n';
}

void capture(HANDLE process, DWORD pid, DWORD crashing_thread, const EXCEPTION_DEBUG_INFO& exception,
    const std::map<DWORD, HANDLE>& threads, const fs::path& report, bool snapshot_fault_memory = false) {
    fs::create_directories(report);
    std::ofstream output(report / L"crash.txt");
    const auto& record = exception.ExceptionRecord;
    output << "VulkanStory native crash\nPID=" << pid << " thread=" << crashing_thread << "\nException=0x"
        << std::hex << record.ExceptionCode << " flags=0x" << record.ExceptionFlags << " address=0x"
        << reinterpret_cast<uintptr_t>(record.ExceptionAddress) << std::dec
        << " firstChance=" << exception.dwFirstChance << '\n';
    for (DWORD i = 0; i < record.NumberParameters && i < EXCEPTION_MAXIMUM_PARAMETERS; ++i)
        output << "Parameter[" << i << "]=0x" << std::hex << record.ExceptionInformation[i] << std::dec << '\n';

    CONTEXT fatal{};
    fatal.ContextFlags = CONTEXT_ALL;
    auto crashed = threads.find(crashing_thread);
    bool have_context = crashed != threads.end() && GetThreadContext(crashed->second, &fatal);
    if (!have_context) output << "Crashing thread context unavailable: " << GetLastError() << '\n';
    else write_registers(fatal, output);
    if (have_context && snapshot_fault_memory) {
        try { capture_fault_memory(process, fatal, record, report, output); }
        catch (const std::exception& error) { output << "Fault-memory snapshot failed: " << error.what() << '\n'; }
    }
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

// DebugBreakProcess creates a new remote-break-in thread in the attached x64
// process. Identify that thread and its breakpoint rather than consuming an
// unrelated application breakpoint while a timeout request is pending. Both
// processes use this Windows installation's x64 ntdll, so export offsets agree.
bool debug_break_addresses(DWORD pid, uintptr_t& entry, uintptr_t& breakpoint, std::ostream& log) {
    HMODULE local = GetModuleHandleW(L"ntdll.dll");
    FARPROC local_entry = local ? GetProcAddress(local, "DbgUiRemoteBreakin") : nullptr;
    FARPROC local_break = local ? GetProcAddress(local, "DbgBreakPoint") : nullptr;
    if (!local_entry || !local_break) { log << "Remote-break exports unavailable: " << GetLastError() << '\n'; return false; }
    const uintptr_t entry_offset = reinterpret_cast<uintptr_t>(local_entry) - reinterpret_cast<uintptr_t>(local);
    const uintptr_t break_offset = reinterpret_cast<uintptr_t>(local_break) - reinterpret_cast<uintptr_t>(local);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) { log << "Remote-break module snapshot failed: " << GetLastError() << '\n'; return false; }
    MODULEENTRY32W module{};
    module.dwSize = sizeof(module);
    bool found = false;
    if (Module32FirstW(snapshot, &module)) do {
        if (_wcsicmp(module.szModule, L"ntdll.dll") != 0) continue;
        if (entry_offset < module.modBaseSize && break_offset < module.modBaseSize) {
            entry = reinterpret_cast<uintptr_t>(module.modBaseAddr) + entry_offset;
            breakpoint = reinterpret_cast<uintptr_t>(module.modBaseAddr) + break_offset;
            found = true;
        }
        break;
    } while (Module32NextW(snapshot, &module));
    CloseHandle(snapshot);
    if (!found) log << "Attached process x64 ntdll breakpoint addresses unavailable\n";
    return found;
}

bool capture_timeout(HANDLE process, DWORD pid, DWORD break_thread, const std::map<DWORD, HANDLE>& threads,
    const fs::path& report, std::string& failure) {
    fs::create_directories(report);
    std::ofstream output(report / L"timeout.txt");
    if (!output) throw std::runtime_error("Timeout text output unavailable");
    output << "VulkanStory owned harness timeout snapshot\nPID=" << pid << " requestedBreakThread=" << break_thread
        << "\nThe watchdog requested this snapshot; Windows supplied no crash exception.\n";
    bool success = true;
    auto fail = [&](const std::string& message) {
        success = false;
        if (!failure.empty()) failure += "; ";
        failure += message;
        output << message << '\n';
    };
    HANDLE dump = CreateFileW((report / L"timeout.dmp").c_str(), GENERIC_WRITE, FILE_SHARE_READ,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (dump != INVALID_HANDLE_VALUE) {
        // All tracked contexts and stacks, no heap and no invented exception record.
        auto type = static_cast<MINIDUMP_TYPE>(MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules |
            MiniDumpWithModuleHeaders | MiniDumpIgnoreInaccessibleMemory | MiniDumpFilterMemory);
        BOOL written = MiniDumpWriteDump(process, pid, dump, type, nullptr, nullptr, nullptr);
        const DWORD error = written ? 0 : GetLastError();
        CloseHandle(dump);
        output << "Minidump=" << (written ? "written" : "failed") << " error=" << error << '\n';
        if (!written) fail("MinidumpWriteDump failed: " + std::to_string(error));
    } else fail("Minidump file unavailable: " + std::to_string(GetLastError()));

    SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
    bool symbols = SymInitializeW(process, report.c_str(), TRUE) != FALSE;
    if (!symbols) fail("Local symbol initialization failed: " + std::to_string(GetLastError()));
    output << "\nThread contexts and stacks (native modules and offsets):\n";
    for (const auto& [id, handle] : threads) {
        output << "Thread " << id << (id == break_thread ? " [REQUESTED DEBUG BREAK]" : "") << '\n';
        CONTEXT context{};
        context.ContextFlags = CONTEXT_ALL;
        if (GetThreadContext(handle, &context)) {
            write_registers(context, output);
            if (symbols) write_stack(process, handle, context, output);
        } else fail("Thread " + std::to_string(id) + " context unavailable: " + std::to_string(GetLastError()));
    }
    if (symbols) SymCleanup(process);
    output << "\nLoaded modules (including driver versions):\n";
    if (!write_modules(pid, output)) fail("Loaded-module enumeration incomplete; see timeout.txt");
    collect_logs(report, output);
    output.flush();
    if (!output) fail("Timeout text write failed");
    return success;
}

void write_timeout_receipt(const fs::path& report, DWORD pid, bool success, const std::string& failure,
    std::ostream& log) {
    const fs::path temporary = report / L"capture-timeout.completed.tmp";
    std::ofstream receipt(temporary);
    receipt << "PID=" << pid << "\nCapture=" << (success ? "completed" : "failed") << "\nReport=timeout\n";
    if (!failure.empty()) receipt << "Error=" << failure << '\n';
    receipt.close();
    if (!receipt) { log << "Timeout completion receipt write failed" << std::endl; return; }
    std::error_code error;
    fs::rename(temporary, report / L"capture-timeout.completed", error);
    if (error) log << "Timeout completion receipt rename failed: " << error.message() << std::endl;
    else log << "Timeout capture " << (success ? "completed" : "failed") << ": " << failure << std::endl;
}

// A narrowly pinned diagnostic for the driver state-clear instruction. This
// borrows only unused DR0 slots; it never patches executable bytes or the disk.
struct DriverStateTrace {
    static constexpr uintptr_t writer_rva = 0xafcf08;
    static constexpr DWORD64 slot_mask = 3 | (DWORD64{0xf} << 16);
    static constexpr unsigned hit_limit = 32;
    struct Slot { DWORD64 original_address; DWORD64 original_bits; };
    bool enabled;
    DWORD pid;
    fs::path report;
    std::ostream& log;
    HANDLE process = nullptr; // Borrowed debug-event handle, closed by Windows.
    uintptr_t module = 0, target = 0;
    unsigned hits = 0, armed = 0, errors = 0, discovery_attempts = 0;
    DWORD last_discovery_error = 0;
    bool capped = false, discovery_pending_logged = false;
    std::map<DWORD, Slot> slots;
    std::set<DWORD> unsupported;

    DriverStateTrace(bool opted_in, DWORD process_id, const fs::path& directory, std::ostream& output)
        : enabled(opted_in), pid(process_id), report(directory), log(output) {}

    void status() {
        if (!enabled) return;
        std::ofstream output(report / L"driver-state-trace-status.txt");
        output << "PID=" << pid << "\nmodule=0x" << std::hex << module << " target=0x" << target << std::dec
            << "\nhits=" << hits << " hitLimit=" << hit_limit << " armedTotal=" << armed
            << " ownedSlots=" << slots.size() << " errors=" << errors << " capped=" << capped
            << "\ndiscoveryAttempts=" << discovery_attempts << " discoveryComplete=" << (target != 0)
            << " lastDiscoveryError=" << last_discovery_error << '\n';
    }

    void error(const std::string& message) { ++errors; log << "Driver state trace: " << message << std::endl; status(); }

    void arm_thread(DWORD id, HANDLE thread) {
        if (!enabled || !target || capped || slots.contains(id) || unsupported.contains(id)) return;
        CONTEXT context{};
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (!GetThreadContext(thread, &context)) {
            unsupported.insert(id);
            error("Thread " + std::to_string(id) + " debug context unavailable: " + std::to_string(GetLastError()));
            return;
        }
        if (context.Dr0 || (context.Dr7 & 3)) {
            unsupported.insert(id);
            std::ostringstream message;
            message << "Thread " << id << " unsupported occupied DR0=0x" << std::hex << context.Dr0 << " DR7=0x" << context.Dr7;
            error(message.str());
            return;
        }
        const Slot original{context.Dr0, context.Dr7 & slot_mask};
        context.Dr0 = target;
        context.Dr7 = (context.Dr7 & ~slot_mask) | 1; // Local execute breakpoint, length one.
        if (!SetThreadContext(thread, &context)) {
            unsupported.insert(id);
            error("Thread " + std::to_string(id) + " DR0 arming failed: " + std::to_string(GetLastError()));
            return;
        }
        slots.emplace(id, original);
        ++armed;
        log << "Driver state trace armed thread " << id << " DR0=0x" << std::hex << target << std::dec << std::endl;
        status();
    }

    void discover(const std::map<DWORD, HANDLE>& threads) {
        if (!enabled || !process || target || capped) return;
        ++discovery_attempts;
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot == INVALID_HANDLE_VALUE) {
            last_discovery_error = GetLastError();
            error("Driver module snapshot failed: " + std::to_string(last_discovery_error));
            return;
        }
        MODULEENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (!Module32FirstW(snapshot, &entry)) {
            last_discovery_error = GetLastError();
            CloseHandle(snapshot);
            error("First driver module entry unavailable: " + std::to_string(last_discovery_error));
            return;
        }
        last_discovery_error = 0;
        do {
            const uintptr_t base = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
            // A LOAD_DLL event can precede publication in the PEB module list.
            // Search the whole later snapshot rather than filtering it to the
            // current event's unrelated DLL and permanently missing the driver.
            if (_wcsicmp(entry.szModule, L"nvoglv64.dll") != 0) continue;
            constexpr std::array<BYTE, 7> expected{0x4d, 0x89, 0xb7, 0x20, 0xb4, 0x02, 0x00};
            std::array<BYTE, 7> live{};
            SIZE_T received = 0;
            if (entry.modBaseSize < writer_rva + expected.size() ||
                    !ReadProcessMemory(process, reinterpret_cast<const void*>(base + writer_rva), live.data(), live.size(), &received) ||
                    received != live.size() || live != expected) {
                // An instruction mismatch is unsupported, never a reason to
                // guess another RVA or modify the driver to obtain a trace.
                capped = true;
                error("nvoglv64.dll writer instruction/size verification failed; tracing disabled");
                break;
            }
            module = base;
            target = base + writer_rva;
            log << "Driver state writer verified module=0x" << std::hex << module << " RVA=0x" << writer_rva
                << " target=0x" << target << " size=0x" << entry.modBaseSize << std::dec
                << " file=" << utf8(entry.szExePath) << std::endl;
            break;
        } while (Module32NextW(snapshot, &entry));
        const DWORD enumeration_error = GetLastError();
        CloseHandle(snapshot);
        if (!target && !capped && enumeration_error != ERROR_NO_MORE_FILES) {
            last_discovery_error = enumeration_error;
            error("Driver module enumeration incomplete: " + std::to_string(enumeration_error));
        }
        if (!target && !capped && !last_discovery_error && !discovery_pending_logged) {
            discovery_pending_logged = true;
            log << "Driver state trace discovery pending: nvoglv64.dll absent from snapshot; retrying at later stopped debug events" << std::endl;
        }
        status();
        if (target) for (const auto& [id, thread] : threads) arm_thread(id, thread);
    }

    void restore(const std::map<DWORD, HANDLE>& threads, bool stopped) {
        for (auto slot = slots.begin(); slot != slots.end();) {
            const DWORD id = slot->first;
            auto thread = threads.find(id);
            if (thread == threads.end()) { slot = slots.erase(slot); continue; }
            const bool suspended = !stopped && SuspendThread(thread->second) != DWORD(-1);
            if (!stopped && !suspended) {
                error("Thread " + std::to_string(id) + " suspension for DR0 restore failed: " + std::to_string(GetLastError()));
                ++slot;
                continue;
            }
            CONTEXT context{};
            context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            bool restored = GetThreadContext(thread->second, &context) != FALSE;
            if (!restored) error("Thread " + std::to_string(id) + " restore context unavailable: " + std::to_string(GetLastError()));
            else if (context.Dr0 != target || (context.Dr7 & slot_mask) != 1) {
                // Another owner changed the slot: leave its new state intact.
                error("Thread " + std::to_string(id) + " DR0 ownership changed; replacement state preserved");
            } else {
                context.Dr0 = slot->second.original_address;
                context.Dr7 = (context.Dr7 & ~slot_mask) | slot->second.original_bits;
                restored = SetThreadContext(thread->second, &context) != FALSE;
                if (!restored) error("Thread " + std::to_string(id) + " DR0 restore failed: " + std::to_string(GetLastError()));
            }
            if (suspended && ResumeThread(thread->second) == DWORD(-1))
                error("Thread " + std::to_string(id) + " resume after DR0 restore failed: " + std::to_string(GetLastError()));
            if (restored) slot = slots.erase(slot); else ++slot;
        }
        status();
    }

    void field(uintptr_t owner, uintptr_t offset, SIZE_T size, std::ostream& output) {
        DWORD64 value = 0;
        SIZE_T received = 0;
        const bool address_valid = owner && owner <= UINTPTR_MAX - offset;
        const bool read = address_valid &&
            ReadProcessMemory(process, reinterpret_cast<const void*>(owner + offset), &value, size, &received);
        const DWORD error = !address_valid ? ERROR_INVALID_ADDRESS : !read ? GetLastError() : ERROR_PARTIAL_COPY;
        const bool valid = read && received == size;
        output << "Owner+0x" << std::hex << offset << " size=" << std::dec << size << " readable=" << valid;
        if (valid) output << " value=0x" << std::hex << value << std::dec;
        else output << " error=" << error;
        output << '\n';
    }

    bool exception(DWORD id, const EXCEPTION_DEBUG_INFO& exception, const std::map<DWORD, HANDLE>& threads) {
        if (!enabled || !target || !exception.dwFirstChance ||
                exception.ExceptionRecord.ExceptionCode != EXCEPTION_SINGLE_STEP || !slots.contains(id)) return false;
        auto thread = threads.find(id);
        if (thread == threads.end()) return false;
        CONTEXT context{};
        context.ContextFlags = CONTEXT_ALL;
        if (!GetThreadContext(thread->second, &context)) {
            error("Single-step context unavailable: " + std::to_string(GetLastError()));
            return false;
        }
        if (context.Rip != target || !(context.Dr6 & 1) || context.Dr0 != target || (context.Dr7 & slot_mask) != 1) return false;
        // Preserve and forward coincident causes (other DR slots, GD/TF or task
        // switching). Only our execution breakpoint is handled by the monitor.
        constexpr DWORD64 other_causes = 0xe | (DWORD64{7} << 13);
        const bool only_ours = !(context.Dr6 & other_causes) && !(context.EFlags & 0x100);
        if (!capped) ++hits;
        if (!capped) try {
            std::ofstream output(report / (L"driver-state-writer-" + std::to_wstring(hits) + L".txt"));
            if (!output) throw std::runtime_error("Writer trace output unavailable");
            SYSTEMTIME utc{};
            GetSystemTime(&utc);
            output << "VulkanStory driver state writer before execution\nUTC=" << std::setfill('0')
                << std::setw(4) << utc.wYear << '-' << std::setw(2) << utc.wMonth << '-' << std::setw(2) << utc.wDay
                << 'T' << std::setw(2) << utc.wHour << ':' << std::setw(2) << utc.wMinute << ':' << std::setw(2) << utc.wSecond
                << '.' << std::setw(3) << utc.wMilliseconds << "Z\nPID=" << pid << " thread=" << id << " hit=" << hits
                << "\nmodule=0x" << std::hex << module << " instruction=0x" << target << " RVA=0x" << writer_rva
                << "\nOwnerR15=0x" << context.R15 << " NewValueR14=0x" << context.R14
                << "\nDR0=0x" << context.Dr0 << " DR6=0x" << context.Dr6 << " DR7=0x" << context.Dr7 << std::dec
                << "\nOnlyRequestedDebugCause=" << only_ours << '\n';
            write_registers(context, output);
            for (const auto& [offset, size] : std::array<std::pair<uintptr_t, SIZE_T>, 5>{{{0, 8}, {0x2b420, 8}, {0x2b428, 1}, {0x2b429, 1}, {0x2b460, 8}}})
                field(static_cast<uintptr_t>(context.R15), offset, size, output);
            SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
            const bool symbols = SymInitializeW(process, report.c_str(), TRUE) != FALSE;
            output << "\nNative caller stack:\n";
            if (symbols) { write_stack(process, thread->second, context, output); SymCleanup(process); }
            else {
                const DWORD error_code = GetLastError();
                output << "Local symbol initialization failed: " << error_code << '\n';
                error("Writer local symbol initialization failed: " + std::to_string(error_code));
            }
            output.flush();
            if (!output) throw std::runtime_error("Writer trace write failed");
        } catch (const std::exception& failure) { error(failure.what()); }
        context.ContextFlags = CONTEXT_CONTROL | CONTEXT_DEBUG_REGISTERS;
        context.EFlags |= 0x10000; // RF executes the same instruction once without retrapping DR0.
        context.Dr6 &= ~DWORD64{1}; // Preserve every other debug status bit.
        const bool resumed = SetThreadContext(thread->second, &context) != FALSE;
        if (!resumed) error("Writer resume context failed: " + std::to_string(GetLastError()));
        log << "Driver state writer hit " << hits << " thread=" << id << " onlyOurs=" << only_ours << " resume=" << resumed << std::endl;
        if (hits >= hit_limit || !resumed) {
            capped = true;
            restore(threads, true);
            log << "Driver state trace disabled after " << hits << " hits" << std::endl;
        }
        status();
        return only_ours && resumed;
    }
};
}

int wmain(int argc, wchar_t** argv) {
    // Production callers use the original three positional arguments. The optional
    // diagnostic flag preserves first-chance AV contexts before CLR/WER handles them.
    const bool capture_first_chance_av = argc == 5 &&
        std::wstring(argv[4]) == L"--capture-first-chance-av";
    if (argc != 4 && !capture_first_chance_av) return 2;
    wchar_t driver_trace_flag[2]{};
    const bool driver_state_trace = capture_first_chance_av &&
        GetEnvironmentVariableW(L"VULKANSTORY_DRIVER_STATE_TRACE", driver_trace_flag, 2) == 1 && driver_trace_flag[0] == L'1';
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
    DriverStateTrace driver_trace{driver_state_trace, pid, report, log};
    bool process_exited = false;
    bool event_stopped = false;
    bool initial_breakpoint = true;
    bool captured = false;
    unsigned first_chance_av_captures = 0;
    bool timeout_attempted = false, timeout_break_pending = false, timeout_receipted = false;
    DWORD timeout_thread = 0;
    uintptr_t timeout_entry = 0, timeout_breakpoint = 0;
    ULONGLONG timeout_break_deadline = 0;
    DEBUG_EVENT event{};
    for (;;) {
        // Only opted-in harness monitors inspect this marker, and never before
        // the initial attachment breakpoint has been handled and continued.
        if (capture_first_chance_av && !initial_breakpoint && !timeout_attempted && process) {
            std::error_code marker_error;
            if (fs::is_regular_file(report / L"capture-timeout.request", marker_error)) {
                timeout_attempted = true;
                if (!debug_break_addresses(pid, timeout_entry, timeout_breakpoint, log)) {
                    write_timeout_receipt(report, pid, false, "Requested breakpoint identification unavailable", log);
                    timeout_receipted = true;
                } else if (!DebugBreakProcess(process)) {
                    write_timeout_receipt(report, pid, false, "DebugBreakProcess failed: " + std::to_string(GetLastError()), log);
                    timeout_receipted = true;
                } else {
                    timeout_break_pending = true;
                    timeout_break_deadline = GetTickCount64() + 8000;
                    log << "Owned timeout break requested for PID " << pid << std::endl;
                }
            }
        }
        if (timeout_break_pending && !timeout_receipted && GetTickCount64() >= timeout_break_deadline) {
            write_timeout_receipt(report, pid, false, "Requested breakpoint not received within 8 seconds", log);
            timeout_receipted = true;
            // Retain identification until exit: a late requested break must
            // still be continued without swallowing application exceptions.
        }
        if (!WaitForDebugEventEx(&event, capture_first_chance_av ? 250 : INFINITE)) {
            const DWORD error = GetLastError();
            if (capture_first_chance_av && error == ERROR_SEM_TIMEOUT) continue;
            log << "WaitForDebugEventEx failed: " << error << std::endl;
            if (timeout_break_pending && !timeout_receipted)
                write_timeout_receipt(report, pid, false, "Debugger event wait failed: " + std::to_string(error), log);
            break;
        }
        event_stopped = true;
        DWORD disposition = DBG_CONTINUE;
        bool done = false;
        bool timeout_captured = false, timeout_success = false;
        std::string timeout_failure;
        switch (event.dwDebugEventCode) {
        case CREATE_PROCESS_DEBUG_EVENT:
            process = event.u.CreateProcessInfo.hProcess;
            driver_trace.process = process;
            threads[event.dwThreadId] = event.u.CreateProcessInfo.hThread;
            if (event.u.CreateProcessInfo.hFile) CloseHandle(event.u.CreateProcessInfo.hFile);
            break;
        case CREATE_THREAD_DEBUG_EVENT:
            threads[event.dwThreadId] = event.u.CreateThread.hThread;
            driver_trace.discover(threads);
            driver_trace.arm_thread(event.dwThreadId, event.u.CreateThread.hThread);
            if (timeout_break_pending && !timeout_thread &&
                    reinterpret_cast<uintptr_t>(event.u.CreateThread.lpStartAddress) == timeout_entry)
                timeout_thread = event.dwThreadId;
            break;
        case EXIT_THREAD_DEBUG_EVENT:
            driver_trace.slots.erase(event.dwThreadId);
            driver_trace.unsupported.erase(event.dwThreadId);
            threads.erase(event.dwThreadId);
            break;
        case LOAD_DLL_DEBUG_EVENT:
            driver_trace.discover(threads);
            if (event.u.LoadDll.hFile) CloseHandle(event.u.LoadDll.hFile);
            break;
        case UNLOAD_DLL_DEBUG_EVENT:
            if (driver_trace.enabled && driver_trace.module == reinterpret_cast<uintptr_t>(event.u.UnloadDll.lpBaseOfDll)) {
                driver_trace.restore(threads, true);
                if (driver_trace.slots.empty()) {
                    driver_trace.module = driver_trace.target = 0;
                    driver_trace.unsupported.clear();
                } else {
                    driver_trace.capped = true;
                    driver_trace.error("Driver unloaded with unrestored DR0 slots; retaining their identity for cleanup");
                }
                driver_trace.status();
            }
            break;
        case EXCEPTION_DEBUG_EVENT:
            log << "Exception code=0x" << std::hex << event.u.Exception.ExceptionRecord.ExceptionCode << std::dec
                << " firstChance=" << event.u.Exception.dwFirstChance << " thread=" << event.dwThreadId << std::endl;
            driver_trace.discover(threads);
            if (initial_breakpoint && event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT) {
                initial_breakpoint = false;
                std::ofstream(report / L"monitor-ready.txt") << "attached";
                if (ready) { SetEvent(ready); CloseHandle(ready); ready = nullptr; }
            } else if (driver_trace.exception(event.dwThreadId, event.u.Exception, threads)) {
                // Only an owned DR0 execution hit without other debug causes.
            } else if (timeout_break_pending && event.dwThreadId == timeout_thread &&
                    event.u.Exception.dwFirstChance &&
                    event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT &&
                    reinterpret_cast<uintptr_t>(event.u.Exception.ExceptionRecord.ExceptionAddress) == timeout_breakpoint) {
                timeout_break_pending = false;
                if (!timeout_receipted) {
                    timeout_captured = true;
                    try {
                        const fs::path diagnostic = report / L"timeout";
                        fs::create_directories(diagnostic);
                        copy_file(report / L"bootstrap-path.txt", diagnostic / L"bootstrap-path.txt", log);
                        copy_file(report / L"game-data-path.txt", diagnostic / L"game-data-path.txt", log);
                        timeout_success = capture_timeout(process, pid, timeout_thread, threads, diagnostic, timeout_failure);
                    } catch (const std::exception& error) { timeout_failure = error.what(); }
                } else log << "Late requested timeout breakpoint continued without another capture" << std::endl;
                // This is the one remote break requested by this monitor.
                // Every other exception retains the ordinary crash policy.
            } else {
                disposition = DBG_EXCEPTION_NOT_HANDLED;
                std::error_code marker_error;
                const bool diagnostic_armed = capture_first_chance_av &&
                    fs::is_regular_file(report / L"capture-first-chance-av.enabled", marker_error);
                if (diagnostic_armed && event.u.Exception.dwFirstChance &&
                        event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
                    ++first_chance_av_captures;
                    // The runtime arms capture at teardown. Caught mod exceptions
                    // during play must not turn the diagnostic into a per-frame dump.
                    const fs::path diagnostic = report / (first_chance_av_captures <= 4
                        ? L"first-chance-av-" + std::to_wstring(first_chance_av_captures)
                        : std::wstring(L"last-first-chance-av"));
                    try {
                        fs::create_directories(diagnostic);
                        copy_file(report / L"bootstrap-path.txt", diagnostic / L"bootstrap-path.txt", log);
                        copy_file(report / L"game-data-path.txt", diagnostic / L"game-data-path.txt", log);
                        capture(process, pid, event.dwThreadId, event.u.Exception, threads, diagnostic, true);
                        log << "First-chance AV capture: " << utf8(diagnostic.wstring()) << std::endl;
                    } catch (const std::exception& error) {
                        log << "First-chance AV capture failed: " << error.what() << std::endl;
                    }
                    // Never swallow this diagnostic exception or suppress terminal capture.
                }
                if (!captured && (!event.u.Exception.dwFirstChance ||
                        event.u.Exception.ExceptionRecord.ExceptionCode == 0xc0000409)) {
                    captured = true;
                    try { capture(process, pid, event.dwThreadId, event.u.Exception, threads, report, diagnostic_armed); }
                    catch (const std::exception& error) { log << "Capture failed: " << error.what() << '\n'; }
                }
            }
            break;
        case EXIT_PROCESS_DEBUG_EVENT:
            process_exited = true;
            driver_trace.slots.clear(); // Debug-event handles close on exit; no live registers remain.
            driver_trace.status();
            log << "Exit code=0x" << std::hex << event.u.ExitProcess.dwExitCode << std::dec << '\n';
            if (timeout_break_pending && !timeout_receipted) {
                write_timeout_receipt(report, pid, false, "Owned process exited before requested breakpoint capture", log);
                timeout_receipted = true;
            }
            if (!captured && event.u.ExitProcess.dwExitCode != 0) {
                // TerminateProcess and some fail-fast reporting paths provide no exception event.
                // Preserve the exit and available logs without inventing a crash stack/subcode.
                captured = true;
                std::ofstream output(report / L"crash.txt");
                output << "VulkanStory abnormal process exit\nExitCode=0x" << std::hex
                    << event.u.ExitProcess.dwExitCode << std::dec
                    << "\nWindows supplied no debugger exception event; no exception context was captured.\n";
                try { collect_logs(report, output); }
                catch (const std::exception& error) { output << "Log collection failed: " << error.what() << '\n'; }
            }
            done = true;
            break;
        }
        if (!ContinueDebugEvent(event.dwProcessId, event.dwThreadId, disposition)) {
            const DWORD error = GetLastError();
            log << "ContinueDebugEvent failed: " << error << std::endl;
            if (timeout_attempted && !timeout_receipted)
                write_timeout_receipt(report, pid, false, "ContinueDebugEvent failed: " + std::to_string(error), log);
            break;
        }
        event_stopped = false;
        // Publish completion only after all threads have resumed; the watchdog
        // can then terminate its child without racing a held debug event.
        if (timeout_captured && !timeout_receipted) {
            write_timeout_receipt(report, pid, timeout_success, timeout_failure, log);
            timeout_receipted = true;
        }
        if (done) break;
    }
    if (ready) { SetEvent(ready); CloseHandle(ready); }
    if (driver_trace.enabled && !process_exited) driver_trace.restore(threads, event_stopped);
    DebugActiveProcessStop(pid);
    if (captured || fs::is_regular_file(report / L"managed-crash.txt")) {
        wchar_t system[MAX_PATH]{};
        GetSystemDirectoryW(system, MAX_PATH);
        fs::path powershell = fs::path(system) / L"WindowsPowerShell/v1.0/powershell.exe";
        std::wstring command = L"\"" + powershell.wstring() + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" +
            (fs::path(argv[0]).parent_path() / L"VulkanStory.CrashReport.ps1").wstring() +
            L"\" -ReportDirectory \"" + report.wstring() + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION collector{};
        if (CreateProcessW(powershell.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                nullptr, report.c_str(), &startup, &collector)) {
            CloseHandle(collector.hThread);
            CloseHandle(collector.hProcess);
        } else log << "Report packaging failed: " << GetLastError() << '\n';
    }
    return 0;
}
