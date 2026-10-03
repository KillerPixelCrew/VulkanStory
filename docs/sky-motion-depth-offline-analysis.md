# Existing sky motion/depth values — 2026-10-01

One bounded offline analysis of existing captured PFM files; no source repair,
build, tests, device probe, new capture, deployment or game launch.

Inputs: dlss-input-capture-20261001-212255/run/attachments, Primary motion color4,
its alpha/writer-depth plane and Primary depth. Used the preceding sky analysis's
normalized ROI (x .12–.22, y .18–.22), respecting bottom-up rows. All three inputs
are 1707x1019; ROI is x 204–374/y 183–223, 7,011 pixels.

| Field | Observed values |
| --- | --- |
| Motion X/Y | Exactly zero throughout; no nonfinite values or magnitude above one pixel |
| Reactive B | Finite, 0.0673828–0.3701172; mean 0.1792563 |
| Writer-depth alpha | Zero throughout |
| Scene depth | Exactly 1 throughout; no nonfinite values |

Results are retained at the run's sky-analysis/motion-depth-results.json.
The raw motion/depth files remain unchanged.

This crop contains far-plane sky with zero static-camera motion. It provides no
evidence of NaN, huge-vector or depth-range corruption there, and no reason to
invert/scalе the motion vector globally. Nonzero reactive coverage is consistent
with transparent cloud content, but does not identify the responsible renderer.
Writer-depth zero means the explicit motion payload does not certify a valid
writer depth in this crop; the retained shader can emit that on its invalid
previous-clip branch. Zero motion remains numerically suitable for a stationary
camera, so the observation alone does not prove it caused the pattern.

Do not generalize this static crop to camera rotation, animated-cloud motion,
the whole frame or SDK-internal history. Direct NGX currently clears the optional
bias-current-color mask; its RG motion input does not consume the separate B
reactive value automatically. Producer history/cloud handling and temporal SR
filtering remain the relevant next source boundaries. No rendering algorithm
change or fix was made in this analysis turn; full acceptance stays open.
