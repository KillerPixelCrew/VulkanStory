# Mod rendering and temporal presentation validation

Validated on 2026-10-15 with Vintage Story 1.22.7, Windows build 26200, an RTX
4070 Laptop GPU and NVIDIA driver 616.92. The supplied pack remained enabled;
174 loaded entries include runtime/input components and VSImGui diagnostics.

- Hidden world run: 39 actions and 29 assertions passed, including persisted
  FSR3-FG startup, TAA, DLSS SR, FSR3 SR/FG, XeSS SR/FG and switching off again.
- Visible, focused world run: 17 actions and 13 assertions passed, including
  DLSS SR/FG, secondary-window topology changes and runtime FG disable.
- Both processes exited with code zero. Both complete Vulkan synchronization
  validation logs contain zero `[error]` entries. No validation errors were filtered.
- The stock atlas draw used a 16384-square, 15-mip source. Its bounded 8-by-4
  sample produced exact expected pixels with one region copy.
- RTSS hooks remained enabled, bypass remained off and the Vulkan overlay render
  pass was active. No RTSS profiles, exclusions or persistent settings changed.
- The matching renderer/native presentation probe also passed secondary-window
  resize/vsync/retirement checks at 512x256 and 400x200 with zero Vulkan errors.
- SDK CPU regression checks passed combined depth/stencil, mip/layer initialization
  and preservation of initialized resource contents.

The exact validated payload was deployed to the established development game
installation; installed file hashes match, with the existing loader configuration
preserved. Raw results, package identities, mod inventories and deployment backup
are in `artifacts/mod-compat-both-fixes-r19/`.

The source review corrected vertex alignment, texture/scissor bounds, external
ownership wait scopes, atlas feedback copies, native/virtual image layouts, NGX
initialization ownership, SDK first-use/transfer barriers, WSI semaphore/fence
lifetimes and latency/presentation host access. The application-local final WSI
signal includes overlay work; secondary HWNDs use the existing Vulkan/DX12 image
handoff, retaining one Vulkan WSI device for the SDK and RTSS.

Contracts and pinned SDK corrections are documented in
[`native/wsi`](../../native/wsi/README.md) and
[`native/streamline/sdk-fixes`](../../native/streamline/sdk-fixes/README.md).
This is finite scenario coverage on the stated hardware; AMD hardware and every
possible interaction among the supplied mods were not independently exercised.
