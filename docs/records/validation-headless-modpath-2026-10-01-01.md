# Isolated staged ModSystem live checkpoint — 2026-10-01

One bounded Bootstrap/Game build -> fresh stage -> isolated asynchronous FSR
capture batch, run once. No tests, native rebuild, installed deployment, source
repairs or second run. Artifacts:
`artifacts/validation/headless-modpath-20261001-191145/`.

Both builds/stage passed. PID 7052 loaded foggy village story's isolated snapshot,
captured all three scheduled PPM/PNG frames and attachment/AO outputs, and closed
automatically with device drain. Result confirms hidden=true/focused=false.

The preceding discovery failure is resolved in this run: stagedModLoaded=true,
worldReady=true, and actual module assembly location is
run/data/Mods/vulkanstory/VulkanStory.Mod.dll. Client/server mod lists include
vulkanstory. The installed standard directory was excluded and explicit isolated
--addModPath discovery supplied the staged copy. No installed DLL/settings or user
process changed. Completion now has module identity/lifecycle evidence as well as
artifact counts; arbitrary renamed/ZIP duplicates remain outside this filter scope.

Final periodic sample: 241 successful FSR SR frames, 239 FG-prepared frames,
791 real/1028 SDK presents (237 additional outputs), current camera/motion.
No new render/cleanup error; known supplied SDK hooks persist. No command scripts,
transition/resize, broad input/platform/proxy or release acceptance inferred.

The mod-isolation path and PNG generation now have bounded runtime evidence.
Installed product payload/archive still predates these source changes. Full
renderer/provider/SDL feature parity and remaining release gates stay open.
No source repair/rerun occurred in this validation turn.
