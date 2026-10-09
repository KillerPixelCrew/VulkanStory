# B0 validation batch — 2026-09-29

Status: **incomplete evidence; B0 remains open.** This was one bounded validation batch. No source was changed during the batch.

## Planned gates

1. Build the managed solution and native bootstrap once.
2. Run the focused managed and native tests once.
3. Stage and deploy only if both test results are known to pass.
4. Launch through the existing game shortcut, inspect startup ordering, and exercise bounded bypass cases only after deployment.

The target was the registered official Vintage Story 1.22.7 installation at `C:\Users\N1GHT\AppData\Roaming\Vintagestory`. Before the batch, `hostfxr.dll` and `VulkanStory/` were absent there, and no game/server/crash-reporter process was found. The desktop and Start Menu shortcuts both targeted that original executable.

## Results

| Gate | Result | Evidence |
| --- | --- | --- |
| Managed solution build | **Passed** | `build-b0.ps1 -Configuration Release` exited 0. Bootstrap, game observer, and test assemblies were produced. |
| Native configure/build | **Passed** | Same script exited 0. CMake selected GNU C++ 16.1.0 and linked `hostfxr.dll` and `fxr_version_tests.exe`. |
| Build diagnostics | **Open cleanup** | C# nullable warnings CS8600/CS8602 in `BootstrapEntry.cs` lines 55–56. GNU warnings on four `GetProcAddress` function-pointer casts in `hostfxr_proxy.cpp` line 185. Zero compile errors. |
| Native version-order test | **Passed** | CTest's `artifacts/native-bootstrap/Testing/Temporary/LastTest.log` records `fxr_version_order` as passed, 0.01 s. |
| Managed focused tests | **Result not captured** | The command was started once with `dotnet test ... --no-build --logger console;verbosity=normal`. The tool wrapper displayed `[object Object]` instead of the command result. A `dotnet` process and child `testhost` were observed; the process later exited, but attaching to it returned an empty exit-code value. No TRX/XML/log artifact was found in the test project. Pass/fail and executed test count are unknown. |
| Stage/deploy/live startup | **Not run** | The planned gate required an observed managed test pass. The official installation and its MFG setup were not modified. |

The native test and build were each run once. The managed test was run once and was not repeated. An attempted read of the app terminal returned “No app terminal session is attached to this thread yet,” so it did not recover the missing output.

## Diagnosis and next implementation turn

The immediate blocker is validation result capture, not an observed product failure. The managed test command yielded a nested tool result that was printed as a JavaScript object string; its output, process session identifier, and exit code were discarded. The process wait confirmed termination but supplied no usable exit code. This turn cannot certify the managed tests or proceed to deployment.

The next implementation turn should add a single B0 validation driver that records every command's exit code and stdout/stderr to stable files (including a TRX result for managed tests) before running dependent gates. Resolve the two C# nullable warnings and review the native cast warnings from this batch while editing source. A later validation turn may run one new bounded batch after that concrete fix. Do not rerun the same managed test in this turn.

Follow-up implementation: `scripts/validate-b0.ps1` now provides this capture and gating; the nullable expression and native function-pointer conversion were changed. This note does not alter the results above. The follow-up source has not yet been built or run.
