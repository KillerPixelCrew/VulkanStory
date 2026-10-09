# Streamline provider-switch options correction — 2026-10-01

Implementation only; no builds, tests, packaging, deployment or game launches.
The preceding goal turn made progress by validating the isolated harness and
recording a concrete provider-switch warning.

## Source evidence

The scripted FSR3 to XeSS run recorded repeated slDLSSGSetOptions at the switch.
Swapchain.Build submits Off before retiring an FSR3 chain. The replacement
Streamline swapchain creation invalidates fgConfigured. A later rebuild can
therefore submit the same Off again without an intervening Streamline Present,
even though DLSS-G has never been enabled.

The local Streamline 2.14.1 DLSS-G guide says options take effect at the next
Present; its header defaults DLSSGOptions.mode to Off. The project uses one
viewport, render-owner-thread options, proxy Vulkan dispatch and full-display
swapchain metadata. Existing camera/motion/UI tags and resource release order
are preserved. SDK source, shipped runtime DLLs and the ABI are unchanged.

## Change

VulkanStorySlSetFrameGeneration now returns success without calling the SDK for
Off when fgEnabled is false, independently of the swapchain dimensions cache.
On still reapplies dimensions/count after recreation. A successful On-to-Off
transition still invokes the SDK; failed calls leave enabled state intact so
subsequent calls retry. fgResourcesMayExist still survives Off until explicit
release, retaining the shutdown-before-NGX dependency. Shutdown now clears
fgEnabled alongside the other lifecycle flags.

## Acceptance still open

This correction is unbuilt and has no new runtime evidence. The preceding
successful harness run remains authoritative, including its warning. A later
bounded isolated run must use a freshly built Streamline bridge to establish
whether that warning is gone. Unsupported SDK hook warnings and full visual,
resize, repeated-toggle and concurrent-session acceptance remain open.
