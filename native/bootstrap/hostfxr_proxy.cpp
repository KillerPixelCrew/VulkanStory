// B0: an apphost-entry proxy, not a replacement CLR or a general-purpose hostfxr implementation.
#include <windows.h>
#include <algorithm>
#include <bit>
#include <filesystem>
#include <mutex>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>
#include "fxr_version.h"

#if !defined(_M_X64) && !defined(__x86_64__)
#error B0 supports the x64 apphost only.
#endif

namespace {
namespace fs = std::filesystem;
using error_writer_fn = void(__cdecl*)(const wchar_t*);
using main_fn = int(__cdecl*)(int, const wchar_t**);
using startup_fn = int(__cdecl*)(int, const wchar_t**, const wchar_t*, const wchar_t*, const wchar_t*);
using bundle_fn = int(__cdecl*)(int, const wchar_t**, const wchar_t*, const wchar_t*, const wchar_t*, std::int64_t);
using set_writer_fn = error_writer_fn(__cdecl*)(error_writer_fn);
HMODULE own_module = nullptr;
LARGE_INTEGER attach_counter{};
DWORD attach_thread = 0;
thread_local error_writer_fn error_writer = nullptr;
constexpr int bootstrap_error = static_cast<int>(0x80008083u);

/// @brief Resolves a borrowed Win32 module to its full path.
/// @details Throws when GetModuleFileNameW fails or fills the fixed-size buffer.
std::wstring module_path(HMODULE module) {
    std::vector<wchar_t> buffer(32768);
    DWORD count = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (count == 0 || count >= buffer.size()) throw std::runtime_error("Cannot resolve module path.");
    return {buffer.data(), count};
}

/// @brief Reads a named process environment value, distinguishing absent and present-but-empty values.
/// @details Throws if the variable grows while its allocated buffer is being read.
std::optional<std::wstring> environment(const wchar_t* name) {
    SetLastError(ERROR_SUCCESS);
    DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
    if (!length) {
        if (GetLastError() == ERROR_ENVVAR_NOT_FOUND) return {};
        return std::wstring{};
    }
    std::vector<wchar_t> buffer(length);
    DWORD count = GetEnvironmentVariableW(name, buffer.data(), length);
    if (count >= length) throw std::runtime_error("Environment changed while reading.");
    return std::wstring(buffer.data(), count);
}

/// @brief Converts a UTF-16 path/value to UTF-8.
/// @details Invalid Unicode raises a runtime error; an empty input returns an empty string.
std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) throw std::runtime_error("Invalid Unicode path.");
    std::string result(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        result.data(), count, nullptr, nullptr);
    return result;
}

/// @brief Quotes and escapes a UTF-8 byte string for the bootstrap JSON-lines log.
std::string json_string(const std::string& value) {
    std::ostringstream out;
    out << '"';
    const char* hex = "0123456789abcdef";
    for (unsigned char c : value) {
        if (c == '\\' || c == '"') { out << '\\' << static_cast<char>(c); }
        else if (c < 32) { out << "\\u00" << hex[c >> 4] << hex[c & 15]; }
        else out << static_cast<char>(c);
    }
    out << '"';
    return out.str();
}

/// @brief Lazily creates the per-process bootstrap log path under local application data or the temporary directory.
const fs::path& log_path() {
    static fs::path value = [] {
        auto local = environment(L"LOCALAPPDATA");
        fs::path directory;
        if (local && !local->empty()) directory = *local;
        else {
            wchar_t temporary[MAX_PATH + 1]{};
            DWORD n = GetTempPathW(MAX_PATH + 1, temporary);
            if (!n || n > MAX_PATH) throw std::runtime_error("No writable diagnostic directory.");
            directory = temporary;
        }
        directory /= L"VulkanStory/Logs";
        fs::create_directories(directory);
        return directory / (L"bootstrap-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
            std::to_wstring(attach_counter.QuadPart) + L".jsonl");
    }();
    return value;
}

/// @brief Appends one native bootstrap event while serializing log writes.
/// @details Logging failures are swallowed so forwarding remains available. This function is called outside DllMain.
void trace(const char* event, const std::string& detail = {}, LONGLONG counter = 0, DWORD thread = 0) noexcept {
    try {
        static std::mutex guard;
        std::lock_guard lock(guard);
        LARGE_INTEGER now{}, frequency{};
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&frequency);
        std::ostringstream line;
        line << "{\"event\":" << json_string(event) << ",\"detail\":" << json_string(detail)
             << ",\"pid\":" << GetCurrentProcessId() << ",\"nativeThread\":" << (thread ? thread : GetCurrentThreadId())
             << ",\"qpc\":" << (counter ? counter : now.QuadPart) << ",\"frequency\":" << frequency.QuadPart
             << ",\"source\":\"native\"}\n";
        HANDLE file = CreateFileW(log_path().c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        auto bytes = line.str();
        DWORD written = 0;
        WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
        CloseHandle(file);
    } catch (...) { /* Keep forwarding functional when logging is unavailable. */ }
}

/// @brief Locates VulkanStory/loader.ini relative to this proxy module.
fs::path config_path() { return fs::path(module_path(own_module)).parent_path() / L"VulkanStory/loader.ini"; }

/// @brief Reads a Bootstrap section string from the package-local loader configuration.
/// @details An overlong configuration value raises a runtime error.
std::wstring config_value(const wchar_t* key) {
    std::vector<wchar_t> buffer(32768);
    DWORD count = GetPrivateProfileStringW(L"Bootstrap", key, L"", buffer.data(),
        static_cast<DWORD>(buffer.size()), config_path().c_str());
    if (count >= buffer.size() - 1) throw std::runtime_error("Bootstrap configuration value is too long.");
    return {buffer.data(), count};
}

/// @brief Reads the registered x64 .NET installation from the architecture-specific registry key.
/// @details Returns no path when the registry value is missing, malformed or empty.
std::optional<fs::path> registered_root() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", 0,
            KEY_QUERY_VALUE | KEY_WOW64_32KEY, &key) != ERROR_SUCCESS) return {};
    // .NET registers the architecture-specific key in the 32-bit registry view.
    wchar_t buffer[32768]{};
    DWORD type = 0, bytes = sizeof(buffer);
    auto status = RegQueryValueExW(key, L"InstallLocation", nullptr, &type,
        reinterpret_cast<BYTE*>(buffer), &bytes);
    RegCloseKey(key);
    if (status != ERROR_SUCCESS || type != REG_SZ || bytes < sizeof(wchar_t) || bytes >= sizeof(buffer)) return {};
    buffer[bytes / sizeof(wchar_t)] = L'\0';
    if (buffer[0] == L'\0') return {};
    return fs::path(buffer);
}

/// @brief Selects the configured, environment, registered or standard x64 .NET root.
/// @details A configured root must be an existing absolute directory; unresolved discovery raises a runtime error.
fs::path select_root() {
    std::wstring explicit_root = config_value(L"DotnetRoot");
    if (!explicit_root.empty()) {
        fs::path root(explicit_root);
        if (!root.is_absolute() || !fs::is_directory(root)) throw std::runtime_error("Configured DotnetRoot is not an existing absolute directory.");
        return root;
    }
    for (auto name : {L"DOTNET_ROOT_X64", L"DOTNET_ROOT"}) {
        auto value = environment(name);
        if (value && !value->empty() && fs::is_directory(*value)) return fs::path(*value);
    }
    if (auto root = registered_root()) return *root;
    auto program_files = environment(L"ProgramW6432");
    if (!program_files || program_files->empty()) program_files = environment(L"ProgramFiles");
    if (program_files && !program_files->empty()) return fs::path(*program_files) / L"dotnet";
    throw std::runtime_error("Cannot discover the installed x64 .NET host. Set Bootstrap.DotnetRoot.");
}

/// @brief Process-lifetime owner of the installed hostfxr module selected by host-directory version precedence.
/// @details Construction rejects a recursive proxy path. The module remains loaded because the runtime may retain its callbacks after a host entry returns.
struct real_host {
    fs::path root;
    HMODULE module;

    real_host() : root(fs::absolute(select_root()).lexically_normal()), module(nullptr) {
        trace("native.proxy.attached", utf8(module_path(own_module)), attach_counter.QuadPart, attach_thread);
        std::optional<vulkanstory::fxr_version> newest;
        fs::path library;
        for (const auto& entry : fs::directory_iterator(root / L"host/fxr")) {
            if (!entry.is_directory()) continue;
            auto version = vulkanstory::fxr_version::parse(entry.path().filename().wstring());
            if (version && (!newest || *newest < *version)) { newest = version; library = entry.path() / L"hostfxr.dll"; }
        }
        if (library.empty() || !fs::is_regular_file(library)) throw std::runtime_error("The selected .NET hostfxr is missing.");
        library = fs::canonical(library);
        if (fs::equivalent(library, module_path(own_module))) throw std::runtime_error("Recursive hostfxr proxy path rejected.");
        module = LoadLibraryExW(library.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) throw std::runtime_error("Cannot load installed hostfxr.dll; Win32 error " + std::to_string(GetLastError()));
        trace("native.hostfxr.resolved", utf8(library.wstring()));
        // Process-lifetime module: the runtime may retain callbacks after the entry call.
    }

    template<class T> T function(const char* name) {
        auto address = GetProcAddress(module, name);
        if (!address) throw std::runtime_error(std::string("Installed hostfxr lacks ") + name);
        static_assert(std::is_pointer_v<T> && sizeof(T) == sizeof(FARPROC));
        return std::bit_cast<T>(address);
    }
};

/// @brief Returns the lazily constructed process-lifetime installed-host owner.
real_host& host() { static real_host instance; return instance; }

/// @brief Temporarily installs a process-local environment value and retains its prior state.
/// @details Destruction restores the prior value only when nobody changed the replacement value during the forwarded host call. The borrowed variable-name pointer must outlive this guard.
struct environment_change {
    const wchar_t* name;
    std::optional<std::wstring> previous;
    std::wstring value;
    bool changed = false;
    environment_change(const wchar_t* key, std::wstring replacement) : name(key), previous(environment(key)), value(std::move(replacement)) {
        if (!SetEnvironmentVariableW(name, value.c_str())) throw std::runtime_error("Cannot arm process-local startup environment.");
        changed = true;
    }
    ~environment_change() {
        // Managed bootstrap removes its own hook before any game child process can start.
        // Do not overwrite unrelated changes the game/another hook made during its lifetime.
        try {
            if (changed && environment(name) == std::optional<std::wstring>(value))
                SetEnvironmentVariableW(name, previous ? previous->c_str() : nullptr);
        } catch (...) {}
    }
};

/// @brief Checks the client executable/assembly identity and package-local bootstrap enable flag.
/// @details The payload file checks and hook setup are performed separately by the startupinfo export.
bool should_activate(const wchar_t* host_path, const wchar_t* app_path) {
    if (!host_path || !app_path || _wcsicmp(fs::path(host_path).filename().c_str(), L"Vintagestory.exe") != 0 ||
        _wcsicmp(fs::path(app_path).filename().c_str(), L"Vintagestory.dll") != 0) return false;
    if (!GetPrivateProfileIntW(L"Bootstrap", L"Enabled", 1, config_path().c_str())) return false;
    return true;
}

/// @brief Records a forwarding failure and calls the current thread's optional error writer.
/// @details Returns the fixed host bootstrap error code without retrying game entry.
int report_failure(const std::exception& error) noexcept {
    trace("native.forwarding.failed", error.what());
    if (error_writer) error_writer(L"VulkanStory could not forward to the installed .NET host. Remove the VulkanStory hostfxr.dll to use normal host discovery; see the bootstrap log.");
    return bootstrap_error;
}
}

/// @brief Stores the current thread's borrowed host error callback and forwards writer registration.
/// @details If installed-host registration fails, the prior locally recorded writer is returned.
/// @param writer Caller-owned callback; its lifetime follows the hostfxr error-writer contract.
extern "C" error_writer_fn __cdecl hostfxr_set_error_writer(error_writer_fn writer) {
    auto previous = error_writer;
    error_writer = writer;
    try { return host().function<set_writer_fn>("hostfxr_set_error_writer")(writer); }
    catch (const std::exception& error) { trace("native.error_writer.unavailable", error.what()); return previous; }
}

/// @brief Arms the optional managed startup hook before forwarding the original apphost startup entry.
/// @details Setup failure bypasses only VulkanStory hook activation. The real host entry is called once with the installed framework root; forwarding errors return the bootstrap error.
/// @param argc Original argument count.
/// @param argv Borrowed original wide-string argument array.
/// @param host_path Original host executable path.
/// @param app_path Original managed application path.
/// @return Original host exit code, or the proxy bootstrap failure code.
extern "C" int __cdecl hostfxr_main_startupinfo(int argc, const wchar_t** argv, const wchar_t* host_path,
    const wchar_t* /*app_local_root*/, const wchar_t* app_path) {
    try {
        auto& runtime = host();
        auto forward = runtime.function<startup_fn>("hostfxr_main_startupinfo");
        trace("native.hostfxr.entry", "startupinfo");
        auto managed = fs::path(module_path(own_module)).parent_path() / L"VulkanStory/managed";
        auto hook = managed / L"VulkanStory.Bootstrap.dll";
        // Setup failures bypass only our hook. Never catch/retry after the real game entry was called.
        std::optional<environment_change> log;
        std::optional<environment_change> hooks;
        try {
            if (should_activate(host_path, app_path) && fs::is_regular_file(hook) &&
                fs::is_regular_file(managed / L"VulkanStory.Game.dll") &&
                fs::is_regular_file(managed / L"profiles/vs-1.22.7-win-x64.json")) {
                auto existing = environment(L"DOTNET_STARTUP_HOOKS");
                std::wstring combined = hook.wstring();
                if (combined.find(L';') != combined.npos) throw std::runtime_error("A startup-hook path cannot contain a semicolon.");
                if (existing && !existing->empty()) combined += L";" + *existing;
                log.emplace(L"VULKANSTORY_BOOTSTRAP_LOG", log_path().wstring());
                hooks.emplace(L"DOTNET_STARTUP_HOOKS", std::move(combined));
                trace("native.startup_hook.armed", "B0 observer");
            } else trace("native.startup_hook.bypassed", "Disabled, other process, or incomplete payload.");
        } catch (const std::exception& error) {
            hooks.reset();
            log.reset();
            trace("native.startup_hook.bypassed", error.what());
        }
        // The apphost selected this DLL as an app-local host. Supply the installed root
        // to the real hostfxr so framework resolution does not use the game directory.
        trace("native.hostfxr.forward", "Original entry; installed framework root.");
        int result = forward(argc, argv, host_path, runtime.root.c_str(), app_path);
        trace("native.hostfxr.return", std::to_string(result));
        return result;
    } catch (const std::exception& error) { return report_failure(error); }
}

/// @brief Forwards the legacy host entry without adding a startup hook.
/// @return Original host exit code, or the proxy bootstrap failure code.
extern "C" int __cdecl hostfxr_main(int argc, const wchar_t** argv) {
    try { return host().function<main_fn>("hostfxr_main")(argc, argv); }
    catch (const std::exception& error) { return report_failure(error); }
}

/// @brief Forwards a single-file bundle entry using the installed framework root.
/// @details Bundles are outside the activation profile and receive no VulkanStory startup hook.
/// @param offset Original bundle header offset.
/// @return Original host exit code, or the proxy bootstrap failure code.
extern "C" int __cdecl hostfxr_main_bundle_startupinfo(int argc, const wchar_t** argv, const wchar_t* host_path,
    const wchar_t* /*app_local_root*/, const wchar_t* app_path, std::int64_t offset) {
    try {
        auto& runtime = host();
        trace("native.startup_hook.bypassed", "Single-file bundle is outside the B0 activation profile.");
        return runtime.function<bundle_fn>("hostfxr_main_bundle_startupinfo")(argc, argv, host_path, runtime.root.c_str(), app_path, offset);
    } catch (const std::exception& error) { return report_failure(error); }
}

/// @brief Records only module/thread/timestamp bookkeeping at process attach.
/// @details Runs under the loader lock. Host discovery, file scanning, managed startup and graphics initialization occur later in forwarded host entry points.
/// @return TRUE; this entry performs no managed or graphics activation.
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        own_module = module;
        attach_thread = GetCurrentThreadId();
        QueryPerformanceCounter(&attach_counter);
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
