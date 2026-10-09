# Retained mesh manager port

Date: 2026-09-30. Implementation only: no builds, tests, probes, packages, or game runs.

The retained `Core/MeshManager.cs` joins the backend's explicit compile list. Its method bodies preserve dedicated/custom attribute order, signed and normalized formats, empty custom-part bindings, SSBO face-record sizing and custom-int pruning, quad index initialization, mapped/device-local allocation, staging order, write diagnostics, deferred deletion, indexed draws, and pointer-sized indirect ranges. Source baseline: `386e0d05386d0b228b439d09aeca851428f7bbf3`.

Game dependencies become aliases to neutral `MeshCustomPartLayout`, `MeshDrawMode`, and `MeshDataConversion` contracts. `GameMeshLayout` reads the original allocation size/interleave/instancing/conversion metadata at the integration boundary; arrays remain shared for synchronous allocation consumption. It does not import the game mesh implementation into the backend. Game draw/conversion values map explicitly. The persistent mapped-VRAM override key becomes `VULKANSTORY_VK_PERSISTENT_MESH_VRAM`; its original heap threshold and fallback are unchanged.

Three existing draw-range cases are carried over with the original expected offsets/counts. Four game-boundary cases cover custom allocation metadata and supported topology mapping. `--mesh-buffers` adds a headless native check for triangle/custom-part buffer allocation, actual mapped vertex writes, SSBO packed-face size/binding omissions, original quad indices, and deletion. The new check does not submit draw commands or render pixels; actual mesh drawing remains to be connected to the retained pipeline/preflight and game facade.

Next validation: backend and game tests, preflight tool build, then one bounded `--mesh-buffers` run. This source increment is unvalidated. Full device facade, game uploads/resource IDs, indexed/instanced/indirect rendering, captures, startup/menu/world routing and all provider integration remain open.

The [first validation batch](validation-p0-mesh-2026-09-30-01.md) compiled the mesh manager and passed all 41 backend cases plus 19 game cases. A game-enum inline-data discovery failure prevented the topology rows; the native check was skipped. The test needs integer attribute data with enum conversion in its body before the next validation turn.

After the concrete repair, [the second batch](validation-p0-mesh-2026-09-30-02.md) passed 41 backend and 22 game cases plus real native buffer checks and an indexed SDL draw/present. Pixel contents, game calls and full mesh feature integration remain open.
