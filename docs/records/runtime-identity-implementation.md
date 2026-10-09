# VulkanStory runtime identity — 2026-10-01

Implementation only; no builds, tests, probes, packages or game launches.
The preceding turn made source progress isolating headless disable/failure paths.

Reviewed active managed/native environment access. Active native bridges already
use VulkanStory names. Remaining managed Optimum switches now use the same suffix
under VULKANSTORY_:

| Area | Active variable(s) |
| --- | --- |
| Latency/Streamline | VULKANSTORY_LATENCY_TRACE, VULKANSTORY_STREAMLINE |
| Validation | VULKANSTORY_VULKAN_VALIDATION, VULKANSTORY_VULKAN_VALIDATION_FEATURES |
| Cache/pipelines/stats | VULKANSTORY_VULKAN_SHADER_CACHE, VULKANSTORY_VULKAN_SYNC_PIPELINES, VULKANSTORY_VULKAN_STATS |
| Entity route | VULKANSTORY_VK_NATIVE_ENTITIES |
| XeSS scheduling | VULKANSTORY_XESS_GPU_GATE |
| AO overrides | VULKANSTORY_AO_INTEGRATION, THICKNESS, CLASS_CHANNEL, NOISE_CYCLE, DENOISE_PASSES, NORMAL_EDGES, TONE, FINAL_POWER, RADIUS (each suffix has the VULKANSTORY_AO_ prefix) |

Capture pipeline selection now reads only the already-migrated
VULKANSTORY_PARITY_DUMP and VULKANSTORY_HEADLESS_FRAMES values. Old environment
aliases are removed, so unrelated donor launch settings cannot alter the new mod.
Validation's default temporary log is vulkanstory-vulkan-validation.log.
Active log prefixes now identify VulkanStory across device/provider diagnostics.
Default values, numeric parsing, algorithms and provider scheduling are unchanged.

Retained shader ABI identifiers (OPTIMUM_BINDING_*, OPTIMUMAO, frame-global block
names and uniform symbols) remain coordinated with the existing source/SPIR-V
corpus. Renaming only one side would break the migrated working renderer. These
internal shader identifiers are not an Optimum launcher/configuration dependency.
Historical donor filenames, license attribution and provenance are retained.

Changes remain unbuilt/unrun. Older test fixtures may still use donor environment
names; their adaptation is deferred with test work. Existing runtime evidence
predates this naming correction. Full feature/release acceptance remains open.
