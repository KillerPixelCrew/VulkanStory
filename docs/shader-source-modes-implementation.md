# Retained shader sources and compiled scene modes

Updated 2026-10-01. Source implementation only; no build, test, probe, package or
game run. New tests remain deferred until integration is complete.

## Source delivery

The retained `.vsh`/`.fsh` files from `sources/shaders` are embedded in the Game
project with their original contents. The retained previous-state `vertexwarp.vsh`
include is embedded separately. Baseline:
`386e0d05386d0b228b439d09aeca851428f7bbf3`.

`ShaderSourceConsumerPatches` supplies matching game-domain stages through the
original `ShaderRegistry.LoadShader` path. Missing retained stages, geometry
stages and other asset domains continue through the original loader. Retained
stages use the original `HandleIncludes` expansion and game shader identities;
`InsertIncludedFile` supplies the retained warp include. Remaining includes,
uniform collection, default prefixes, compilation, linking, registry ownership
and custom sampler setup remain with the original lifecycle.

The new `graphics-shader-sources` group is mandatory in complete graphics
composition. It is still dormant because the complete startup plan is unfinished.

## Compiled mode ownership

After original default prefixes, the adapter defines `TAAMOTION`,
`TAAMOTIONLOCATION` and `OPTIMUMAO` from session settings and the original SSAO
attachment count. FXAA follows the retained render-scale/temporal rule. Unrelated
greedy-mesh defines are zero because the port retains original game mesh producers.
No Optimum configuration or runtime dependency is introduced.

Registered shader loading clears published motion/AO modes and starts a new link
ledger. Only successful links using both retained writer stages enter that ledger.
After a successful original load result, all required terrain, entity, standard,
instance, decal and cube-particle programs must match the current requested mode
before it is published to the owned temporal/AO state. Failed or interrupted loads
leave modes disabled. GTAO `auto` remains conditional on the effective temporal
pipeline; explicit `gtao` selects it with the SSAO G-buffer enabled.

This publication certifies a compiled mode, not complete frame inputs. The adapter
explicitly leaves dense motion coverage false until all producers are connected.

## Remaining integration

Complete scene/profile registration, remaining motion writers and content renderers,
shader asset override policy/native shader corpus delivery and host factories remain
open. The later asset-origin policy gives external/patched stages precedence and
routes them through the rewriter. Required-writer/shared-include replacements
disable stock feature publication until an explicit compatibility adapter supplies
their temporal/G-buffer contracts.
No complete shader load, native pixels, SDK inputs or runtime mode switch has been
accepted in the new host.
