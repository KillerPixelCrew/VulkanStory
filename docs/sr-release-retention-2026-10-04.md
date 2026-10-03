# SR release retention — 2026-10-04

DIRECT implementation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
Visible launch approval remains pending; no prepared launcher was executed or
changed. SDK-03 remains open.

FSR3 SR native destruction previously deleted its wrapper after an SDK error;
the managed context discarded that return code and cleared its handle. Native
destruction now returns failure while retaining the wrapper, including missing
destroy function with a live effect. Managed destruction checks the result,
retains its handle/failure and rejects retries. XeSS SR context destruction now
does the same for negative SDK errors, preserving its existing nonnegative
warning convention. Both contexts remain owned by the deferred retirement queue;
its existing terminal failure handling retains the failed entry and tail.

RuntimeUpscalers previously logged backend shutdown errors, then removed/cleared
their owners and continued dependent cleanup. Shared ReleaseBackend now records
and propagates failure. Bring-up cleanup removes a provider only after successful
shutdown. Runtime disable and settings retirement no longer suppress release
errors. Registry Dispose removes each successfully shut-down backend individually,
leaving a failed backend and the remaining providers retained. All subsequent
prepare/bring-up/plan/evaluate/disable/settings/dispose paths reject a recorded
registry release failure. Session's existing dependency-chain cleanup can therefore
stop before dependent device/SDL release instead of receiving false success.

Source inspection followed native return paths, XeSS header/API warning convention,
queue ownership transfer, registry removal and session failure propagation. No
builds, tests, probes, packages, client runs or deployment ran; no tests added.
Current binaries/prepared visible stage predate this increment. A matching rebuilt
FSR3 bridge is required for the new native retention behavior. No ABI signature,
rendering algorithm, counter or supported-mode policy changed. Failed destruction
remains terminal; this does not promise recovery after partial SDK destruction.
Other provider/native ownership paths and injected-failure acceptance remain open.

| File | SHA256 |
| --- | --- |
| native/fsr3/bridge.cpp | `84D98DB101843567F44C6BA0B651967CD68E853AB3D87DB65E20C83A39FEDA6A` |
| src/VulkanStory.Render.Vulkan/Upscale/Fsr3Backend.cs | `34E6CFDBEAFBC8C18AFDFF62C2094FDAC8E08EFE67A9AEB20A01372EB525B23F` |
| src/VulkanStory.Render.Vulkan/Upscale/XessBackend.cs | `23899C4B6B634B5E368D164F0190EC97ACA7C374A1BBE27AC515068EDA0ED104` |
| src/VulkanStory.Game/RuntimeUpscalers.cs | `A34468B6A63BF748C9E6FE87CC973764533A49A573C0C35C3E87DB2F1888B9FA` |
