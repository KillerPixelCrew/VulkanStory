# Existing sky attachment analysis — 2026-10-01

One bounded offline artifact-analysis batch. No source repairs, builds, tests,
device probes, new captures, deployment or game launches. Inputs are the existing
DLSS-only dump from `dlss-input-capture-20261001-212255/run/attachments`.

Decoded Primary color0 and Slot22 SR-output PFM float data, respecting retained
bottom-up rows. Selected the same normalized sky rectangle (x 0.12–0.22,
y 0.18–0.22) in each image. Computed linear RGB luminance and subtracted a
nine-row local mean per column to expose short vertical structure. Derived
grayscale residual plots use 128 + residual*4000 with nearest 4x display scaling.
These are diagnostic plots, not tone-mapped game screenshots or visual edits.

Results and plots are under the existing run's `sky-analysis/` directory:

| Image | Size | Pixel residual RMS | Row-mean residual RMS | Coherence ratio |
| --- | --- | ---: | ---: | ---: |
| Primary scene | 1707x1019 | 0.00324027 | 0.00028087 | 0.08668 |
| DLSS SR output | 2560x1528 | 0.00274473 | 0.00112149 | 0.40860 |

Inspected both residual plots. Primary predominantly shows fine-grained dither;
SR output contains distinct horizontal structures matching the final-image
observation. The structures exist in the SR-output texture before final
composition/FG. This narrows the next investigation to the reconstruction/input
boundary, rather than screenshot encoding, display scaling or final UI composition.

The image resolutions differ, and a nine-row filter spans different angular
scales. These ratios are descriptive, not a pixel-parity metric, formal causal
proof or claim that the source is entirely free of structure. One static crop
cannot separate DLSS's reconstruction behavior from inaccurate jitter/motion/
history/input contents. The current dump contains relevant motion/depth inputs;
use those existing artifacts and source contracts before another run.

No algorithm change was justified or made here. Full visual/feature acceptance
remains open. Original raw files are preserved unchanged; results.json records
the method, dimensions, ROI and per-row residual means.
