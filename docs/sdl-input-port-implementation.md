# SDL game input extraction

Date: 2026-09-29. Implementation only: no build, test, probe, package, or game run.

`VulkanStory.Game/Input` now contains the retained SDL scancode map, touch tap/drag/long-press state machine, and physical/controller/touch input arbitration. The source algorithms come from the renderer's SDL helpers and `ClientPlatformWindows` input patches at baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. The existing generated client source was used to extract those patched method bodies; it is not a compile or runtime dependency.

This is an adapter migration. Added key/button ownership sets and controller reference counts belong to `GameInputBridge`. The original platform still owns its handler lists, mouse coordinates, wheel total, and key-release history. The bridge preserves physical repeat dispatch, controller reference counting, suppression of releases while another source holds an action, independent events per handler, the 200 ms secondary-key rule, UTF-16 text dispatch, and wheel accumulation once per event. Focus loss releases physical/touch actions before notifying the original focus listeners. The future sidecar must cancel touch and release controller mapper actions as part of the same transition.

`GamePlatformBindings` caches typed Harmony field accessors and closed delegates to original mouse-position, frame, and close callbacks. Binding verifies declared field types and managed void method signatures; it performs no game callback invocation. `GamePlatformBindingProfile.Validate1227` is called by the static profile tool after official hash checks and resolver registration. Its new check has not yet run. Rendering through the bound original frame callback requires complete graphics/window routing first; no live caller or active patch group is added here.

Host dependencies are explicit callbacks for physical-input activity and SDL cursor pixels. The original controller-hint/configuration singleton and OpenTK window mouse query are removed. The macOS wheel clamp is omitted under the inherited Windows/Linux platform scope. Keyboard/touch namespace changes are mechanical. A controller release helper collapses remaining source counts on shutdown/focus cleanup while respecting physical/touch holds; this added lifecycle operation needs targeted acceptance.

The 2026-09-30 source repair also makes wheel sensitivity an explicit callback. The sidecar reads the original setting when a real wheel event arrives; fixtures supply a value without game-settings initialization. This dependency adaptation preserves wheel arithmetic and live setting changes. Its behavior validation remains pending.

Game references use the official client/API/OpenTK assemblies with copying disabled. The neutral SDL project has no new game dependency. Nothing loads Optimum or modifies the official game assemblies.

Remaining work: build and verify the new binding contracts against official 1.22.7, reuse input arbitration/touch fixtures, complete the SDL sidecar/event dispatch and GUI text-target/IME coordination, transplant controller mapping/device integration, and implement every mandatory startup/graphics/window patch group before activation. This source increment does not close G0 or G3.

Subsequent evidence: [the first bounded validation batch](validation-g0-input-2026-09-29-01.md) passed a clean build, 17 existing bootstrap tests, the official startup baseline, and the new platform binding metadata checks. Input behavior and actual cached delegate/field access remain unverified.
