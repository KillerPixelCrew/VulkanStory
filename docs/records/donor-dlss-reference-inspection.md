# Donor DLSS capture/source reference — 2026-10-01

Source/file inspection only; no build, test, device probe, package or game launch.

Located an existing donor headless final frame at
D:/Coding/VulkanStory/tmp/dlss-headless-frames/frame-000100.ppm.
It is raw PPM; the image viewer cannot decode that format. No conversion or new
capture was performed in this turn. No adjacent run result/settings/log was found
in that directory, so the folder name alone does not certify DLSS mode, camera,
world, resolution, frame history or comparability to the rewrite capture.

Other existing donor PNGs under tmp/ao-upscaler-fix show AO debug output and do
not provide a clear-sky reference. They must not establish native visual parity.
The saved optimum-before-dlss-headless.json is a prior configuration snapshot;
it must not be treated as the effective configuration for the raw frame.

Compared donor and rewrite NgxDlssFeature source: creation node/visibility masks,
dimensions, quality, creation flags and output-subrect disable follow the retained
helper call sequence. Per-frame colour/output/depth/motion resource pointers,
jitter, motion scale, optional input clearing, subrects and exposure follow that
same path. The rewrite's documented rendered frame-time input is additional.
This source comparison is not SDK/GPU execution or a proof of equal images.

The existing donor PPM provides a possible inspection reference for a later
bounded offline comparison; settings/camera differences must remain explicit.
The rewrite's valid sky writer output does not remove its horizontal sky pattern.
Do not change motion scale, jitter sign, dithering or reconstruction algorithms
based only on an unqualified historical image. Full acceptance remains open.