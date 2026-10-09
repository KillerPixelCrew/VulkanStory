# Accumulated integration compile checkpoint — 2026-10-01

One bounded compile batch completed once. Artifacts:
`artifacts/validation/integration-compile-20261001-200040/`.

| Target | Result |
| --- | --- |
| Game and Contracts/Input/SDL/full Vulkan dependencies | Passed, zero warnings/errors |
| Ordinary client Mod | Passed, zero warnings/errors |
| Server Input.Companion and shared Input | Passed, zero warnings/errors |
| Streamline native bridge against local SDK 2.14.1/Vulkan SDK 1.4.357.0 | Passed |

Compiled Game includes the new declared mod-pass API/host, motion scope and
world cleanup boundaries, SDL icon startup and embedded PromptFont Skia/Cairo
rendering. The fresh native bridge includes the idempotent disabled-DLSS-G options
correction. This supersedes the unbuilt status of those source increments only.

No tests, scripts/package staging, ZIPs, deployment, runtime probes, GUI/font
previews or game launch. No source fixes or second batch. Companion packaging
script changes have not run, and existing ZIP/staged/installed payloads remain
older. Game behavior, custom-pass execution, actual glyph appearance,
integrated-server negotiation and provider-switch warning resolution remain
unverified by this compile checkpoint. Full requested port acceptance stays open.
