# B0 implementation record

Date: 2026-09-29. **The B0 shim and managed hook ran before normal client startup. The first real Harmony observation failed while resolving `OpenTK.Mathematics` before the game's own resolver was registered.** See the [third validation batch](validation-b0-2026-09-29-03.md). The early resolver and binding cleanup fix are now in source and await a later validation turn.

## Selected bootstrap prototype

The Windows prototype is an app-local `hostfxr.dll` proxy. The .NET apphost checks its application directory for this library before consulting environment/global runtime locations. Its exported `hostfxr_main_startupinfo` is called outside `DllMain` and before CoreCLR starts managed code.

The proxy resolves the installed x64 .NET host, loads it by absolute path, sets a process-local `DOTNET_STARTUP_HOOKS` entry, then forwards the original entry call exactly once. It supplies the installed .NET root because the apphost initially treats an app-local hostfxr as its runtime location. It never starts a separate CLR or invokes the managed game's `Main` itself.

The native DLL forwards the four apphost startup/error-writer exports used by this prototype. It is not a full implementation of every hostfxr embedding API. Self-contained applications and arbitrary hosting/COM consumers are outside this initial profile; deployment refuses to replace any existing `hostfxr.dll`.

This choice does not use or modify `version.dll`, so the current MFG Enabler can keep that filename. The third validation batch proved activation from the normal game shortcut. Coexistence with the full MFG Enabler package remains unvalidated.

Primary source evidence, pinned to .NET 10.0.0:

- [App-local host resolution](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/fxr_resolver.cpp): application-directory lookup precedes installed host discovery.
- [Apphost entry dispatch](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/corehost.cpp): calls the startup-info export and propagates its exit code/error writer.
- [Hostpolicy startup-hook handling](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/hostpolicy/hostpolicy_context.cpp): consumes the hook environment while preparing the runtime.
- [Public host ABI](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/hostfxr.h): export signatures and thread-local error writer.

## Official reference inspected

The registered official installation is `C:\Users\N1GHT\AppData\Roaming\Vintagestory`, version 1.22.7. Its runtime configuration requests .NET 10 and Windows Desktop 10. The API metadata reports `ShortGameVersion = 1.22.7`; its shipped Harmony is the thin 2.4.2 build, and OpenTK.Windowing.Desktop is 4.9.4.

`profiles/vs-1.22.7-win-x64.json` records SHA-256 identities for the executable, entry/client/API assemblies, runtime/dependency configuration, Harmony, and OpenTK.Windowing.Desktop. These are local reference fingerprints, not a claim of publisher signature verification. Unknown inputs bypass managed observation before loading the game integration.

Read-only targeted decompilation confirmed:

- `ClientProgram.Main(string[])` and private `Start(ClientProgramArgs, string[])`.
- The original sealed `ClientPlatformWindows` and its `Logger` constructor.
- The normal `AttemptToOpenWindow`, screen startup, and platform startup sequence.

No original game binary was changed or loaded for execution during implementation.

The existing MFG proxy is `C:\Users\N1GHT\Optimum\version.dll`, SHA-256 `E9CA3587854EEB723E0579F7DDF6CFB1E6CF4BED79B0D75BC716003ED98FE040`. Its adjacent status file identifies RTXMFG. That file is historical diagnostic evidence, not a new runtime check. This implementation leaves the existing installation alone.

## Implemented source

| Area | Behavior |
| --- | --- |
| Native apphost proxy | Installed-host discovery, numeric/SemVer host selection, four forwarding exports, client filtering, bypass, process-local hook arming, native QPC markers. |
| Framework-only managed startup hook | Idempotence, profile verification, narrow dependency resolution, shared diagnostics, child-process hook cleanup, integration version check. |
| Harmony observer | Exact overload binding for seven startup targets, transactional installation/rollback, entry/return markers, active game data path after normal initialization. |
| Diagnostics | One JSONL stream per process with shared QPC clock and OS thread IDs. Logs normally go to `%LOCALAPPDATA%\VulkanStory\Logs`. |
| Developer tooling | Separate build, staging, fresh deployment, and trace interpretation scripts. No automatic game launch. |
| Test source | Hook-environment preservation, reference mismatches/traversal, exact overload binding, native host version ordering. |

The prototype deliberately keeps vanilla rendering and input. It does not include a ModSystem, Vulkan/SDL device activation, or the renderer migration yet. The process status is available under `AppContext.GetData("VulkanStory.Bootstrap.Status")` for the later mod entry.

## Bounded validation driver for the next validation turn

Run this **once** from the rewrite directory in the next validation turn. The command is documented, not executed in this implementation turn. It builds, tests, and stages the revised payload without touching the official game installation; the prior B0 payload remains installed there.

```powershell
.\scripts\validate-b0.ps1 -Configuration Release -RunId b0-20260929-04
```

The driver creates a fresh `artifacts/validation/<RunId>/` and records each child process's stdout, stderr, timeout and exit code in `summary.json`. It now requires a TRX file showing at least nine focused managed tests passed, including the new early-dependency cases, and the current CTest log showing the native test passed. A failed or unknown result stops before staging. The build defaults to Ninja with an override available. The driver does not launch the game. The B0 files from the third run remain in the original game directory; the fresh-deployment command cannot be used there until those owned files are removed.

The third batch recorded a warning-free build and passing original B0 tests. The new early-resolver tests and revised source have not yet been built or run.

Use the original shortcut for the live case. Inspect its actual JSONL path:

```powershell
.\scripts\read-b0-trace.ps1 -Path '<actual bootstrap log>' -Expected Active -RequireExit
```

`Enabled=0` under `[Bootstrap]` in the deployed `VulkanStory/loader.ini` bypasses our managed hook while forwarding the normal host entry. An unsupported game profile also bypasses observation; it does not select a different DLL. If host discovery itself fails, the log/error writer identifies that failure and removing the VulkanStory-owned proxy restores default apphost discovery.

## One bounded B0 validation batch

Plan and execute the following as one batch in a validation turn. Stop on an unexpected dependency/build failure rather than changing code or rerunning in that turn.

1. Build once, run the new managed tests and native version-order test once, then stage/deploy once if those prerequisites succeed.
2. Normal client startup/exit through the existing shortcut, using a working directory outside the game folder. Confirm the ordered trace, same entry OS thread, one managed/game entry, unchanged ordinary menu behavior, data path, and successful actual process exit.
3. Loader-disabled client startup/exit, a controlled unsupported-profile case using only the mod's profile copy, and a non-client process case. These are distinct preplanned cases; preserve all logs. Any altered loader/profile file must be restored from the staged payload afterward.
4. Co-installed RTXMFG loader case if its full required package is available in the validation target. Do not equate a lone copied proxy with a complete supported MFG installation. Record this case as unavailable if not prepared; do not claim it passed. Actual MFG rendering is a later G3 check once Vulkan/Streamline are attached.
5. Record files installed, exact process exits, trace ordering, runtime module path, failures/skips, and whether the host loaded from the normal shortcut. Restore/remove only the recorded B0-owned files if cleanup is needed; inspect absolute paths before any recursive cleanup.

This prototype supports the narrow installed Windows framework-dependent reference. Broader hostfxr embedding APIs, early mod-manager enable policy, optional native providers, Linux activation, and the final package updater are later work. A B0 pass does not mark those complete.
