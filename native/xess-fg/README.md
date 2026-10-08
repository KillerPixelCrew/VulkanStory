# XeSS frame-generation bridge

Migrated from `native/optimum-xess-fg` at source revision
`386e0d05386d0b228b439d09aeca851428f7bbf3`; reference sources remain in
`native/migrated/optimum-xess-fg`. Own symbols and runtime filename use
`VulkanStoryXessFg`, and diagnostic environment keys use `VULKANSTORY_XESS`.
SDK entry points, resource formats, matrix/frame ABI, queue ownership and
presentation behavior are retained. Preserve inherited provenance/notices.

From a Windows developer shell with `g++` and `VULKAN_SDK` headers:

```powershell
./native/xess-fg/build.ps1 -SdkRoot <XeSS-3-SDK-root> -Output <absolute-output-directory>/VulkanStoryXessFg.dll
```

The SDK root supplies `inc/xess_fg/xefg_swapchain_d3d12.h` and the other original
XeSS/XeLL headers. The retained script accepts explicit SDK/output paths;
it does not download SDKs or copy vendor runtimes. Players receive built
authorized binaries in the private native RID directory and never run it.
The bridge loads `libxess_fg.dll` and `libxell.dll` from its own directory;
retain their vendor-supplied dependencies and redistribution notices in packaging.

Native compilation and scoped NVIDIA execution/handoff are recorded in the [Roadmap](../../docs/ROADMAP.md) and [development evidence](../../docs/development-evidence.md). Those results apply to their recorded source and hardware. Later fixes need their own validation; visible quality/cadence, latency and eligible Intel coverage remain open. A managed ABI fixture alone does not establish these gates.
