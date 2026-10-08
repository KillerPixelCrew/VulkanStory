# Historical port checkpoint — 2026-10-01

The [ROADMAP](ROADMAP.md) and [development evidence](development-evidence.md) are authoritative for current status and payloads. The descriptions below retain the October-1 checkpoint; later Options/controller work supersedes its pending/source-only statements. The bulk
renderer/provider/SDL source migration is integrated; full feature parity remains
open. Detailed chronology is retained in linked records and the archived plan.

## Implementation and evidence at this checkpoint

| Area | Current state | Remaining roadmap items |
| --- | --- | --- |
| Early loading, official game/Harmony/SDL startup, menus and graphics API | Active routing; normal-shortcut and hidden world use recorded | DEL-01/03, SDL-01 |
| Vulkan device/resources/shaders/graph/draws/readback and native scene/content routes | Compiled and used to render foggy village story | REN-02..05 |
| Temporal motion/TAA, GTAO/SSAO, bloom/god rays/composition and separate UI | Integrated; sky output4/depth1 verified in a static crop | REN-01..04 |
| DLSS/XeSS/FSR3/FSR4 SR, DLSS-G/XeSS-FG/FSR3 FG and latency | All five bridges compiled; scoped SR/FG preparation/provider switches recorded | SDK-01..04, HW-01..02 |
| SDL focus/scaling/fullscreen/cursor/clipboard/IME/touch and controllers | Source/compile integrated; interactive coverage incomplete | SDL-01..03, PORT-01 |
| PromptFont glyphs/profiles/gyro/haptics/keyboard and input companion | Embedded/integrated; companion loaded on integrated server | SDL-02..03, NET-01 |
| Screenshot/timelapse/AVI and mod-pass/motion API | PNG/PPM works; AVI patch and vanilla stage host accepted | CAP-01..02, API-01 |
| Mod/settings and drop-in/update/remove package flow | Fresh ZIP/scripts delivered; settings need an Options-menu tab accessible from a loaded world; removal syntax only | UI-01, DEL-01..04 |
| Direct-GL mod adapters and new Linux activation/package | Implementation outstanding | GL-01..03, LINUX-01 |

## Payload identities

Candidate at this checkpoint: [delivery-refresh-20261001-223159](validation-delivery-refresh-2026-10-01-01.md).
Managed build has zero warnings/errors; stage/archive/script syntax checks passed.
The later controller world/damage-ownership fix is **source-only** and absent from
that ZIP (PORT-01). Installed Game DLL inspected during regroup was older (15:37)
and differed from the candidate DLL (22:32). A normal launch does not update it.

## Evidence to use

- [All native bridges/full shader corpus](validation-native-and-shaders-2026-10-01-01.md):
  native warnings retained; this is build evidence, not all-vendor execution.
- [Native-resolution baseline world](validation-graph-native-world-2026-10-01-01.md).
- [FSR3 to XeSS](validation-integration-harness-2026-10-01-01.md) and
  [FSR3 to DLSS](validation-provider-handoff-2026-10-01-01.md) scoped handoffs.
- [Sky motion repair](validation-motion-variant-2026-10-01-01.md) and
  [raw SR-output sky analysis](sky-pattern-offline-analysis.md): writer repair is
  supported; the horizontal sky structure remains unresolved with FG off.
- [Pending world-damage ownership](controller-world-damage-ownership.md).
- [Current candidate](validation-delivery-refresh-2026-10-01-01.md): removal tool
  source hash matches the ZIP; execution/rollback acceptance remains open.
- [Donor character-creation capture review](validation-donor-existing-frame-2026-10-01-01.md):
  excluded as a loaded-world parity reference. Do not substitute it for foggy village story.

Hidden prepared-frame/SDK-present counts do not prove visible FG interpolation or
pacing. One static crop does not prove all moving-scene motion. New test writing is
deferred until integration is finished; routine checks remain isolated from the
user's installation/save. Full completion is not claimed.
