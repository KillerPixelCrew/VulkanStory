# Exact attachment-frame inputs — 2026-10-01

Implementation turn only: no build, test, package, device probe or game launch.

The headless attachment dump now writes frame-inputs.json at the same capture
boundary as its PFM attachments. It includes device/temporal/world frame IDs,
applied jitter, reset, rendered delta, dither seed, game width and SR result.
This removes the need to infer captured inputs from a one-second runtime sample,
which can belong to an adjacent rendered frame.

The opt-in sky capture retains the exact inverse jittered view-projection and
previous unjittered view-projection arrays submitted to the native pass, the
resolved uniform block/offsets, render dimensions, motion slot, previous-world
capture state and draw submission result. Matrices are column-major Mat4f arrays.
No producer mathematics, shader, NGX scale, jitter sign or resource layout changed.
Matrix retention and uniform metadata allocation occur only with parity dumping
explicitly enabled. Named nonfinite values remain serializable for diagnosis.

Source review found the native sky pass matrix construction and upload consistent
with the donor path. This does not explain the prior sky crop's writer-depth zero:
reactive coverage could be from OIT merge, an invalid previous-clip branch or
other state. A submitted draw is not proof of per-pixel coverage. Consumers must
compare skyMotion.frameId with the captured frame IDs; null or an older sky record
must not be treated as a current producer sample.

Pending: compile these changes and the earlier NGX rendered-delta addition in a
later bounded validation turn. Existing attachment captures remain unchanged.
The DLSS sky pattern and full renderer/SDL/provider acceptance remain open.