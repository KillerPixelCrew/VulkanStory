# Runtime frame diagnostics and capture — 2026-10-01

Implementation only. No build, tests, probes, packages, deployment or game run.

Existing presentation text already reports current SR evaluation and FG preparation,
and SDK observers count actual reported presents. Existing screenshot routing reads
the composed Vulkan framebuffer. Neither output was captured by the prior bounded
world run. Add opt-in collection without changing provider selection or scene rules.

Set VULKANSTORY_RUNTIME_DIAGNOSTICS to an absolute output directory. Default is off.
Once per second after Present, the session appends runtime-<pid>.jsonl with requested
and effective SR/FG, successful SR-frame count, FG input-preparation count, camera/
motion/current-frame validity, configured DLSS count, SDK-reported DLSS presents,
real/SDK presentation counters, status text and capture frame/path. Counts are
separated: input preparation and configured multipliers do not prove interpolation.

After LevelFinalize and 60 attached-world frames, capture one composed world image
through the existing screenshot service to world-rendered-<pid>.png. Capture occurs
before FG tagging to avoid synchronous readback splitting tagged command work.
It is a rendered source framebuffer, not physical scanout or a generated frame.
Logical screenshot scaling follows the game's existing screenshot setting. Each
world entry resets the one-capture gate; logs identify capture errors. Diagnostic
file failures disable that optional output, without affecting normal rendering.

FG preparation status now retains its concrete wait/unavailable reason, surfaced
in presentation text and the JSON record. Provider logic, real-scene gating and
SDK dispatch stay unchanged. This helps identify why a provider is selected yet
has not prepared current inputs.

New source is unbuilt/undeployed. Installed candidate remains the successful
runtime-world-20261001-153130 payload. Next bounded validation should enable this
output for foggy village story and explicitly choose the desired provider settings
through existing configuration. Preserve current settings beforehand. Full parity,
visual correctness, actual SR/FG, other providers/platforms and release packaging
remain open; no capture or provider acceptance is claimed by this source increment.
