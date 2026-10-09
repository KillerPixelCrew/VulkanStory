# Native runtime loading source increment — 2026-10-01

Implementation only. No builds, tests, packaging, SDK calls or game runs.

The renderer now creates its shaderc binding using the selected compiler's
absolute path. Deployed packages require the compiler under their private RID
directory; standalone developer outputs retain Silk's default fallback when no
local compiler is present. Compiler identity considers that selected native
directory before the application folder. Failed compiler initialization disposes
the binding. Staging no longer duplicates shaderc under managed/runtimes.

This uses the constructor/native context exposed by the pinned Silk.NET 2.23.0
sources: [Shaderc constructor](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/SPIRV/Silk.NET.Shaderc/Shaderc.gen.cs)
and [DefaultNativeContext](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Core/Silk.NET.Core/Contexts/DefaultNativeContext.cs).

XeLL P/Invokes explicitly register the renderer's existing resolver before the
first call. That resolver now selects packaged libxell.dll in addition to NGX;
missing optional XeLL remains an unavailable capability through TryCreate.

Provider bridges and SDK runtime loads share an absolute-path helper. Windows
loads use UseDllDirectoryForDependencies and SafeDirectories to allow sibling SDK dependencies
without changing the global process search path. SDL applies the same flags in
its independent resolver. Linux keeps its normal absolute-path loading behavior.
Driver-owned NGX discovery remains with the retained driver/shim code.

XeSS-SR's explicit development override is now VULKANSTORY_XESS_LIBRARY and
requires an absolute path. It no longer reads the Optimum-specific override.

The first production compile found invalid managed enum names in the initial
implementation. Both loaders now use the .NET 10 enum names above; that source
correction has not been rebuilt. Native dependency resolution, optional
provider availability, feature execution and SDL/shader operation in the complete
game package still require acceptance. Existing evidence counts are unchanged.
