# P0 mesh manager validation — first batch, 2026-09-30

Status: **backend build/tests passed; game fixture discovery failed; native mesh check skipped.** One bounded validation batch ran once. No implementation source changed, no commands were rerun, and no game/native mesh preflight or deployment occurred.

The planned batch was Release backend tests, Release game tests, then on success the preflight tool build and one `--mesh-buffers` run. Both test commands completed within 60-second limits. The [summary](../artifacts/validation/p0-mesh-20260930-01/summary.json), [backend stdout](../artifacts/validation/p0-mesh-20260930-01/backend.stdout.log), [backend stderr](../artifacts/validation/p0-mesh-20260930-01/backend.stderr.log), [backend TRX](../artifacts/validation/p0-mesh-20260930-01/backend.trx), [game stdout](../artifacts/validation/p0-mesh-20260930-01/game.stdout.log), [game stderr](../artifacts/validation/p0-mesh-20260930-01/game.stderr.log), and [game TRX](../artifacts/validation/p0-mesh-20260930-01/game.trx) are retained.

| Gate | Result |
| --- | --- |
| Backend | Exit 0; compiled retained mesh manager/native buffer check; 41/41 tests passed, zero skips. Includes three original indirect-range cases. |
| Game integration/test compilation | Succeeded, including neutral metadata conversion. |
| Game tests | Exit 1; 19 cases passed, one discovery failure. The custom allocation metadata case passed. Three intended draw-mode rows were not discovered as executable cases. |
| Preflight build/native check | Skipped after the game test failure. No native allocation/write result is claimed. |

Diagnosis from the saved stack trace: the `InlineData` values in `DrawModePreservesSupportedTopology` reference `VintagestoryAPI.EnumDrawMode`. xUnit decodes that custom attribute before the test module initializer registers the official resolver, producing a `CustomAttributeFormatException` wrapping a missing `VintagestoryAPI` assembly. The same assembly resolves correctly during the other executed test bodies. This is a discovery boundary failure, not evidence that topology mapping is wrong.

Concrete next implementation fix: use integer-only inline data and cast to the original/neutral enum types inside the test body after initialization. Keep all three expected mappings and keep official assemblies outside copied test/product output. Then validate once in a later turn. Native mesh allocation, actual draws, game routing and full P0/G1 acceptance remain open.
