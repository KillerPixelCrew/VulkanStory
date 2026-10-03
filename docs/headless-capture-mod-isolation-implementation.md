# Inspectable scheduled captures and isolated ModSystem discovery — 2026-10-01

Implementation only: no builds, tests, probes, packages, snapshots or launches.
Reading the existing later PPM with the image viewer failed because the viewer
does not support that encoding; no new rendering run was performed.

Scheduled captures now retain original PPMs for SSIM and write PNG sidecars from
the same already-read BGRA pixels. PNG row order is top-down; PPM's retained GL
row order is unchanged. No second GPU readback or shader/resource changes. This
allows inspection of the later scheduled world frames rather than only the early
one-off diagnostic image. PNG failure does not count as a complete scheduled frame.

The harness formerly refused a staged ordinary ModSystem that differed from the
installed one. That prevented testing later mod/UI changes without deployment.
Headless-only mod discovery now validates and patches the official CollectMods
return boundary, filtering only the standard installed Mods/vulkanstory directory
before info/assembly loading. The ordinary loader discovers the staged copy in the
isolated data Mods directory. Launcher always copies that staged ModSystem and
passes its explicit path to bootstrap validation. Normal game discovery is untouched;
no installed files are overwritten and no alternate game folder is created.

The scope is the standard installed directory owned by this package. Arbitrarily
renamed/ZIP duplicates are not claimed covered. ModSystems still use the game's
normal loader/events; no substitute lifecycle or injected game member. The patch
belongs to the existing headless startup owner and rolls back with it.

No native ABI changed. Rebuild Game and stage with the latest matching native
bundle from headless-cleanup-20261001-184218 for subsequent isolated validation.
PNG/orientation, staged-mod discovery and later-frame visual completeness remain
unverified. Commands/transitions/input/platform/proxy/release gates remain open.
Installed user game/settings/process untouched.
