# Production compile batch — 2026-10-01, third batch

Validation turn after the preceding implementation fixes. One Game build only,
with Release configuration and official VintageStoryPath. No reruns, source
fixes, tests, native/shader builds, packages, installs or game launches occurred.

Result: Game exit 1, **98 errors, 0 warnings**. Input, Contracts, SDL and the full
Vulkan backend built successfully before Game compilation failed. Mod, Bootstrap
and the optional server companion were not rebuilt in this batch; their last
recorded results still apply.

Complete log and result JSON:
`artifacts/validation/production-compile-20261001-031427/`.
Unique diagnostics and the full build summary were inspected. The compiler now
reaches method bodies beyond the previous declaration errors; the larger error
count is not evidence that 98 independent features are broken.

## Diagnosed source groups

| Group | Errors | Required source work |
| --- | --- | --- |
| Remaining renderer imports and resulting overload/member errors | 81 | Import VulkanStory.Render.Vulkan in Blit, Celestial, CloudMap, Clouds, Decals, Motion, Oit, Particles, Sky and UiSeparation partials; inspect any genuine uniform overload mismatch after restoring types |
| ClientSettings references | 12 | Use its official Vintagestory.Client.NoObf namespace in AO/session settings/framebuffer setup |
| Internal game classes | 2 | Reflect exact SystemRenderNightSky and SystemRenderFrameBufferDebug identities, preserving original visibility |
| OpenGL query fallback signatures | 2 | Correct ref/out usage for the official OpenTK GenQueries/GetQueryObject overloads |
| Shader filename | 1 | Access original internal Shader.Filename through a guarded field reference instead of a direct source member |

The original snapshot confirms ClientSettings is public under Client.NoObf and
Shader.Filename is internal. The retained native declarations remain in the
renderer root namespace. These are boundary adaptation issues; do not remove the
affected rendering features or change official game visibility to bypass them.

Earlier sky/base-class, Common API imports and declaration-level renderer fixes
are no longer reported at their previous locations. Full Game compilation and
all live renderer/provider/package acceptance remain open. Fix these groups in
the next implementation turn before another compile batch.

## Following implementation correction — 2026-10-01

Added the renderer root namespace to the ten remaining adapter partials and the
official Client.NoObf import to settings consumers that lacked it. Session's
incorrect fully qualified ClientSettings name now refers to Client.NoObf.

Night sky and framebuffer debugger resolve by exact reflected identities from
the official ClientMain assembly, with ClientSystem assignability guards. Shader
filename updates use a guarded Harmony field reference to the existing internal
string. No official visibility or injected members are required.

Query fallback calls now use the bound OpenTK overloads' out parameters. Wrapper
signatures and guarded CLR by-reference call anchors remain unchanged. Retained
rendering passes, resources, shader modes and draw algorithms are preserved.

No builds, tests, probes, packages or game runs occurred during this source-fix
turn. The fixes cover the diagnosed groups but remain uncompiled; any remaining
uniform overload or other downstream errors must be assessed from a later batch.
The 98-error table remains the authoritative last compile result.
