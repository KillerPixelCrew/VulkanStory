# Linked motion-output certification — 2026-10-01

Implementation turn only; no builds, tests, probes, packages or runtime launch.

After fixing the owned sky/liquid native variant prefixes, reviewed the remaining
owned shaders. The scene-SSAO prefix already supplies SSAOLEVEL. The seven required
scene motion writers declare motion output4 or2 in their retained native sources.

Scene readiness previously checked the prepared mode records and successful links,
without checking the linked interface's actual written fragment output. Added a
backend query for that interface (no game types or Harmony in the renderer).
Required chunkopaque/chunktopsoil/entityanimated/standard/instanced/decals/particlescube
links now certify requested motion mode only if the intended attachment is written.
A mismatch logs the pass/location and fails the existing complete-scene mode gate.
Unrelated shaders are not required to write motion.

Owned liquid motion also checks its linked output before adopting the program. Its
existing failure path deletes the rejected program and refuses producer completion.
This complements the sky pass's existing reflected output guard. No rendering math,
shader source, resource layout, NGX input convention or fallback algorithm changed.
The checks run at shader link/load time, not for each scene draw.

Source changes are unbuilt. The preceding motion-variant capture remains evidence
for its earlier payload: valid sky output4/depth1, visible sky pattern unresolved.
These new guards certify linked output presence, not per-pixel motion correctness,
liquid draw coverage or broad renderer/provider acceptance. Further integration and
acceptance work remains open; no new tests were written.