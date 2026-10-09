# Indexed mesh preflight and discovery repair

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages, or game runs.

The concrete fixture repair replaces game enum values in `InlineData` with integers. The original/neutral casts occur inside the test body after resolver initialization. All three triangle/line/line-strip expected mappings remain. The previous discovery failure is retained in [its validation record](validation-p0-mesh-2026-09-30-01.md); this repair is unvalidated.

`--sdl-mesh` extends the retained draw/present preflight with one indexed triangle. It allocates position and 32-bit index buffers through `MeshManager`, writes them through its mapped/staged path, translates a position-input shader, obtains a graphics pipeline keyed by the actual mesh vertex-layout ID, binds and draws through the retained manager, and uses the existing dynamic-rendering/submission/blit/presentation chain. A device-idle teardown boundary retains mesh buffers through GPU completion. The original fullscreen `--sdl-draw` path remains available.

The new path is source-only and uses a hidden window. It does not read back or inspect pixels, exercise actual game shaders/mesh objects, test multi-draw/instancing, validate mesh ID ownership/recycling at the public bridge, or attach game rendering/providers. The standalone native buffer check remains available to isolate allocation/metadata/mapped-write/SSBO behavior.

Next bounded validation should run backend and game tests, build the tool, and run `--mesh-buffers` plus `--sdl-mesh` once each with process time limits. Passing those checks would establish only their exercised backend boundaries; game/menu/world routing and full port acceptance remain open.

The [next bounded batch](validation-p0-mesh-2026-09-30-02.md) passed all planned gates: 41 backend tests, 22 game tests, clean tool build, native buffer checks and hidden indexed draw/present. The discovery repair is verified; rendered pixel contents and game routing remain unverified.
