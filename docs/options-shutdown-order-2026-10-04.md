# Options shutdown ordering and crash verification — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
The preceding hidden Options run rendered the panel but crashed during late
texture cleanup after session disposal and routing removal. Raw verifier pass
was invalid as lifecycle acceptance.

StopAndDrain now clears Options owners and stops controller/text UI before setting
stopping=true and before FG/SR/device teardown. TextureConsumer routing and the
live GameGraphicsAdapter can therefore delete GUI LoadedTextures through Vulkan.
Later subgroup rollback sees cleared Options state instead of performing the
first GUI release after the device has gone. GUI/controller cleanup now belongs
to the dependency chain: a failure marks terminal cleanup failure, stops new
work and retains the device/SDL/remaining owners rather than proceeding into
dependent releases. This also covers the direct SDL StopAndDrain callback, not
only ProcessRuntime.Shutdown.

verify-headless-result.ps1 now adds ClientCrash when the fresh isolated
data/Logs/client-crash.log exists and is nonempty. The harness does not copy source
logs. The check applies to both legacy and scenario results, including visible
mode, independently of capture success/process exit. It does not reject ordinary
warnings by broad log-string matching.

Source inspection followed RequireDevice's active-routing requirement, session
stop callers, Options cache detachment, controller close ownership and existing
terminal failure propagation. No builds, syntax probes, tests, client runs,
packages or deployment ran. No tests added. A later bounded validation must
check rejection of the retained crash artifact and run the corrected Options
child once; shutdown success must be established from both result and logs.
Header/footer fit, main-menu, physical/controller input and full FG remain open.
Visible approval remains pending; prepared launchers/stage and installed
game/settings/save were unchanged. Existing binaries predate this source.

| File | SHA256 |
| --- | --- |
| src/VulkanStory.Game/GameRenderSession.cs | `DE9C791ABAE3CC9369E930DB0257519BFED4B22F06239B8FF92D6FE042E4268F` |
| scripts/dev/verify-headless-result.ps1 | `EC615C5B7513D72570982FA83A2387B6DEF660BF7A615498ED8850610B102335` |
