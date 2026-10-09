// SPDX-License-Identifier: GPL-3.0-only
// Ported from ReScaleFrame runtime/backends/fsr/src/fsr4_compat.h (same author, GPL-3.0-only).
// Changes: no log callback; the vendor is read from the DXGI adapter the bridge already matched
// to the Vulkan LUID and created the device on (avoids ID3D12Device::GetAdapterLuid, whose
// aggregate return is ABI-sensitive under MinGW).
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <memory>

struct Fsr4Compatibility;
/// @brief Private, module-version-guarded INT8 enablement for NVIDIA/Intel adapters.
/// @details The returned lease must outlive the FFX context. Code/trampoline modules stay
/// resident; dropping the last lease restores native capability answers. No adapter
/// information is changed outside the SDK.
/// @param module Loaded amd_fidelityfx_upscaler_dx12.dll (SDK 2.3.0, upscaler 4.1.1.2740 only).
/// @param adapter DXGI adapter that owns device.
/// @param device D3D12 device the FFX context will be created on.
/// @return The device-scoped lease, or null when compatibility is refused.
std::shared_ptr<Fsr4Compatibility> VulkanStoryFsr4EnableInt8(HMODULE module,
    IDXGIAdapter1* adapter, ID3D12Device* device);
