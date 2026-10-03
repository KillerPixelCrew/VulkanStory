# Dependency notice inputs

`native/` and `managed/` supply the notice directories consumed by runtime
preparation/staging. `sources.json` records the fetched source URLs or original
NuGet package entries and SHA256 values. License texts are retained as supplied.

SDL's native DLL contains revision `SDL-3.5.0-a8591d9`; its notice comes from
that upstream revision. The SDL binding notice comes from the installed
ppy.SDL3-CS 2026.722.0 package's recorded source commit.

Shaderc and its glslang/SPIRV dependencies use the revisions selected by the
Silk.NET 2.23.0 build submodule and Shaderc DEPS. The native package's repository
commit metadata alone is not treated as a Shaderc source revision.

Silk managed notices come from its NuGet-recorded source commit. Microsoft
DependencyModel 9.0.9 and PlatformAbstractions 3.1.6 license/third-party notices
are copied from the installed packages. Vendor SDK notices are selected separately
by prepare-native-bundle.ps1; retained project provenance is staged separately.

These are delivery inputs, not runtime execution evidence. Final redistribution
inventory review remains open, including native compiler/runtime contributions.
