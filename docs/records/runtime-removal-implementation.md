# Ownership-aware removal tooling — 2026-10-01

Implementation turn only: wrote script, staging entry and player instructions.
No parser/build/test/probe/package or installation/removal/game run was executed.

scripts/remove-runtime.ps1 accepts GameDirectory and a fresh external
BackupDirectory. It reads the updater receipt or extracted package inventory,
requires the expected schema/product/profile and core ownership entries, validates
SHA256/path entries and plans only unchanged owned files. Modified files remain;
a modified hostfxr.dll aborts before any move so its payload is not detached.
Paths stay within the named game/backup roots, reject parent traversal/alternate
streams and refuse existing reparse-point traversal. Saves/settings/version.dll
are outside the allowed payload prefixes.

Supports -WhatIf. Actual removal requires game/server/crash reporter closed.
It moves individual files with LiteralPath rather than deleting a directory tree.
Activation moves first; dependencies follow. A backup removal.json records each
move and preserved files. Failure attempts to restore dependencies before the
activation proxy without overwriting a concurrently-created file. Backup remains
for recovery. Empty directories are retained. Existing arbitrary loader restoration
is not inferred; the updater only overwrites already-owned proxies.

The stage now includes VulkanStory/tools/remove-runtime.ps1. README-client REMOVE
instructions explain preview, backup and preserved files. This complements the
normal-shortcut drop-in/update/disable flow; no separate game installation.

Source only: parser/packaging and actual recovery/removal remain unverified.
The latest ZIP predates both this script and the handheld-shadow prefix increment.
Renderer/provider/SDL visual acceptance, sky artifact and hardware/platform gaps
remain open. No new tests were written or user installation files changed.