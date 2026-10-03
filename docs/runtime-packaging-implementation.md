# Runtime packaging source increment — 2026-10-01

Implementation only. No builds, tests, packages, installations or game runs.

- Added `scripts/build-runtime.ps1`: builds Bootstrap, Game (including the renderer,
  SDL, contracts and input dependencies), ordinary client mod and server companion,
  plus the native hostfxr bootstrap. It does not build through a test project.
- Added `scripts/stage-runtime.ps1`: takes explicit prepared native binaries,
  native/managed license directories and the full compiled shader directory.
  Stages the current first-party split, approved private managed dependencies,
  controller database, provenance and client mod. Optional server payload is kept
  outside the client Mods tree. All file inputs are resolved before creating the
  fresh output directory; the package inventory records SHA256 values.
- Staging rejects missing native core binaries, missing native shader programs,
  empty variants/stages and invalid or mismatched manifest blob paths/hashes.
  These checks do not certify the variant combinations or optional SDK bundles.
- Early resolution now pins the approved SDL/Silk/transitive managed dependencies
  to `VulkanStory/managed`; official game/Harmony dependencies remain game-owned.
- Bundled controller mappings resolve relative to the Game assembly in
  `VulkanStory/assets`, followed by the existing user mapping override.
- Updated loader comment and package layout. Observation-only B0 staging refuses
  to package the current bootstrap, which now prepares the complete runtime.

The new scripts have not run. Shaderc probing is provisioned with its NuGet native
layout but not accepted live. Provider/native redistribution inventory and notices
must be prepared from the licensed sources; no donor/game assemblies are selected
by the managed staging inventory. All-provider binary completeness, package
operation, menu/world rendering and install/update/removal acceptance remain open.
No existing evidence count or acceptance milestone changes in this increment.

## Full native input inventory source — 2026-10-01

Added packaging/native-win-x64.json with explicit core, DLSS, FSR 3/4, XeSS,
XeSS-FG and Streamline binary groups. Runtime staging now requires every listed
file and copies that inventory rather than an arbitrary native directory tree.
Hardware/provider capability fallback remains runtime behavior; a full-feature
delivery cannot accidentally omit an entire provider's binaries.

prepare-native-bundle.ps1 resolves bridge outputs, core native DLLs, signed SDK
redistributables and release Streamline plugins from explicit input directories.
It selects named SDK notice files and supplied core notices, preflights input
existence, copies into a fresh native/licenses bundle and writes a SHA256 inventory.
Driver-owned nvngx.dll is not bundled. No game or donor assemblies are selected.

Neither new preparation nor revised staging ran in this implementation turn.
Additional DLL dependencies, exact SDK release/plugin matching, notices
completeness, private NGX plugin discovery and live feature operation remain open.
The SDK source checkout lacks release Streamline plugin DLLs; a release runtime
directory must be supplied from the corresponding SDK distribution.

## Streamline dependency selection correction — 2026-10-01

Static inspection confirmed NGX already passes the private native directory as
its feature search path; its algorithm requires no change. The native bridge
loads PCL along with Reflex/DLSS-G, while the retained delivery recipe also ships
nvngx_dlssg.dll. Added sl.pcl.dll and nvngx_dlssg.dll to the explicit inventory
and the release NGX notice to preparation. This restores the dependencies selected
by the working baseline instead of silently dropping them during the port.

The local Streamline source headers identify version 2.14.1. No matching complete
release package is present in the inspected SDK directory. Requested its location
from the user under the DLSS Frame Generation skill's local release SDK rule.
No DLLs were copied from legacy game/test output as a substitute. Preparation,
release-specific dependency verification and runtime evaluation did not run.

DLSS-G status for this increment: native bridge build passed in the preceding
batch; optional fallback/setup/activation/tagging/visual/pacing execution NOT RUN.
Integration quality remains an unaccepted MVP source candidate. No runtime options
or counts changed. Native Streamline release delivery remains open.

## Client/server archive source increment — 2026-10-01

Added package-runtime.ps1. It reads the staged inventory, rejects missing/changed
files and escaping paths, then writes separate client and optional server ZIPs
with their own inventories and archive hashes. Client entries retain the existing
game-directory layout; companion entries omit the optional-server staging prefix
so they extract into a server's Mods directory. Source provenance notices are now
staged for the companion as well as the client. No extra game folder or launcher
is introduced. Recorded acceptance status is preserved.

SDL source inspection confirmed the single queue, clipboard/window services and
focus input cleanup are already connected; no speculative SDL algorithm changes
were made. This increment ran no builds, tests, probes, staging, archives or games.
The archive script remains unexecuted; the matching release SDK input and actual
complete package/startup/provider operation remain open.

## Matching Streamline SDK located — 2026-10-01

Found `C:\Users\N1GHT\Downloads\streamline-sdk-v2.14.1.zip` and read its archive
inventory/version header. It contains production bin/x64 interposer/common,
Reflex/PCL/DLSS-G plugins, nvngx_dlssg.dll, NvLowLatencyVk.dll and release notices.
The header declares 2.14.1. Unpacked the SDK resources into the fresh directory
`sdk/streamline-2.14.1`; no downloaded code or DLL was executed.

Archive SHA256:
`92C4D954631A1710DA86CA3FA8D5034F2B9503838C95FC4AE977AE149319781B`.

The earlier SDK-location request is resolved; no further user input is needed for
that path. Native bundle preparation now defaults to the selected SDK's bin/x64,
checks the expected header version and compares each supplied Streamline runtime
against that SDK's production DLL hash. NvLowLatencyVk and the Reflex notice are
selected from the release directory. Development plugin binaries are not selected.

This is SDK resource preparation plus source editing only. Bundle preparation,
staging, archives, builds, tests and game execution did not run. The new matching
checks remain unexecuted. Other redistribution notices and actual full runtime
loading/menu/world/provider operation remain open.

## Core and managed notice inputs — 2026-10-01

Collected 12 upstream/package notice files under packaging/notices/native and
managed, with source URLs/package entries and SHA256 records. SDL revision was
read from the native file's static revision string; Shaderc dependency revisions
were read from Silk's pinned build submodule and Shaderc DEPS. Microsoft notices
were retained directly from the installed package versions. Staging now includes
the dependency notice source record alongside the copied texts.

This turn prepared notice source inputs only; no tests, builds, native calls,
bundles, staging, archives or games ran. These input directories now allow the
existing preparation/staging workflow to proceed in a later bounded batch. Full
runtime operation and final redistribution inventory review remain open.

## Owned payload deployment source — 2026-10-01

Inspected the actual official game directory: the older B0 payload and its
five-file ownership receipt are still present. Prior automatic approval review
rejected removal with `blocked by policy`. No removal attempt was repeated.

Added deploy-runtime.ps1 for a backed-up update of verified owned files. It checks
package/reference hashes, scopes writes to the client payload, refuses unknown
or changed code collisions, preserves loader.ini and leaves version.dll alone.
Dependencies/shaders precede Game/Bootstrap/proxy writes; identical files are
skipped. Backups and planned writes are recorded outside game/package roots;
failed copying restores overwritten files without deleting newly added files.
The old B0 receipt/logs remain in place. Optional server files are not deployed.

Implementation only: no deployment, build, test, package or game run occurred.
The new script remains unexecuted; actual startup and rollback operation remain
unverified. This provides a non-deleting update path, not a retry of the rejected
cleanup commands.
