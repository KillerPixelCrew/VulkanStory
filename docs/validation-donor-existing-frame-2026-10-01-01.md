# Existing donor frame offline review — 2026-10-01

One bounded offline artifact review only. No source repair, build, tests, package,
new capture, installation or game launch.

Source: D:/Coding/VulkanStory/tmp/dlss-headless-frames/frame-000100.ppm.
SHA256:42EB9B12B31DF5E030C8AED37A9505B1FE67394B7E8A686F9541004FC422468B.
Read original P6/255 RGB bytes (2560x1528) and encoded a PNG for viewing, reversing
GL readback rows for display as the retained PPM writer stores rows unchanged.
No colour manipulation or pixel resampling. Original PPM unchanged.
Artifacts: artifacts/validation/donor-existing-frame-20261001/result.json and
its donor-frame-000100.png. The viewer resized its preview to2048x1222; original
PNG retains full dimensions.

The frame shows character creation, no loaded terrain and a blank minimap. It is
not comparable to the rewrite's loaded foggy village story scene. Its effective
SR settings/world/camera/history remain unverified; the folder name cannot certify
active DLSS evaluation. Do not use it to claim the sky pattern predates the rewrite
or to accept full visual parity. This eliminates that frame as a loaded-world
reference and supports no rendering algorithm change.

The rewrite's raw Primary/SR-output captures and valid output4 sky writer remain
the actionable evidence. Visible sky structure, broad feature acceptance and latest
controller-world source compile remain open. No further batch was run.