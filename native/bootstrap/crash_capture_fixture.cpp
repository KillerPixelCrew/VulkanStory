#include <windows.h>
#include <filesystem>
#include <fstream>
#if defined(_MSC_VER)
#include <intrin.h>
#endif
#include "crash_monitor_client.h"

// Separate CPU-only process: validates Windows exception delivery without starting the game.
LONG CALLBACK handled_exception(EXCEPTION_POINTERS* details) {
    return details->ExceptionRecord->ExceptionCode == 0xe0421111 ? EXCEPTION_CONTINUE_EXECUTION : EXCEPTION_CONTINUE_SEARCH;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 2;
    std::filesystem::path report(argv[2]);
    std::string status = start_crash_monitor(argv[1], report, argv[3]);
    std::ofstream(report / L"fixture-ready.txt") << status;
    if (status != "attached") return 3;
    if (wcscmp(argv[4], L"fastfail") == 0) {
#if defined(_MSC_VER)
        __fastfail(7);
#else
        __asm__ __volatile__("int $0x29" : : "c"(7) : "memory");
#endif
        return 4;
    }
    if (wcscmp(argv[4], L"access-violation") == 0) {
        auto memory = static_cast<volatile DWORD*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READONLY));
        if (!memory) return 5;
        *memory = 1;
        return 6;
    }
    if (wcscmp(argv[4], L"raise-fastfail") == 0) {
        EXCEPTION_RECORD record{};
        record.ExceptionCode = 0xc0000409;
        record.ExceptionFlags = EXCEPTION_NONCONTINUABLE;
        record.NumberParameters = 1;
        record.ExceptionInformation[0] = 7;
        CONTEXT context{};
        RtlCaptureContext(&context);
        record.ExceptionAddress = reinterpret_cast<void*>(context.Rip);
        RaiseFailFastException(&record, &context, 0);
        return 7;
    }
    if (wcscmp(argv[4], L"handled") == 0) {
        auto handler = AddVectoredExceptionHandler(1, handled_exception);
        RaiseException(0xe0421111, 0, 0, nullptr);
        RemoveVectoredExceptionHandler(handler);
    }
    if (wcscmp(argv[4], L"detach") == 0) {
        while (!std::filesystem::exists(report / L"finish.txt")) Sleep(20);
    }
    return 0;
}
