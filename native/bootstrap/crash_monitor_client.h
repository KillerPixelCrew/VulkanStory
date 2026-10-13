#pragma once
#include <filesystem>
#include <string>

// Called from the forwarded host entry, never from DllMain.
std::string start_crash_monitor(const std::filesystem::path& executable,
    const std::filesystem::path& report, const std::filesystem::path& bootstrap);
