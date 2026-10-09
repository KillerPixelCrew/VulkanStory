# Workflow

- A turn is either implementation or validation. In an implementation turn, edit code only: no builds, tests, packages or game runs.
- A validation turn runs one planned batch (build, package, checks) once and reports the result. If it fails, diagnose from the logs and fix it in a later implementation turn; don't rerun.
- Routine renderer checks use the headless harness with an isolated copy of the **foggy village story** save, never development superflat. The harness must be silent (all six audio levels zeroed). DLSS-FG checks run visible and focused; everything else stays hidden.
- Never deploy to, open, focus or close the user's game unless the user asks.
- Tests are deferred until the renderer, SDL and provider integration is finished.

# Architecture

- Read `docs/ROADMAP.md`, `docs/architecture.md` and `docs/bootstrap-and-installation.md` before implementing.
- Build against the official, unmodified game assemblies and never ship them. No Optimum launcher, transplant/delta pipeline or modified game assemblies.
- Keep game internals and Harmony out of the renderer, SDL and provider code. Bootstrap code must not initialize graphics under the OS loader lock.
- Extra state lives in VulkanStory objects, not injected members, unsealed game types or added virtual slots.
- Migrated renderer, shader, SDL and provider code stays close to the original; no optional algorithm or resource-layout refactors.
- Keep attribution and licenses of migrated code. A rename doesn't change a file's license.
- Vendor SDKs come only from the `sdk/` submodules (Streamline runtimes via `scripts/fetch-streamline-release.ps1`); nothing builds from outside the repo.

# Docs

- `docs/ROADMAP.md` lists only the current release, features, known defects and deferred work. No rules, verification backlogs, evidence logs or progress diaries.
- The user tests deployed builds. Record the defects they report; don't track verification gaps.
- No per-session or dated record files in `docs/`. Raw run output stays in the ignored `artifacts/` folder.
- Before saying a doc is updated, re-read the whole file and fix everything stale. Don't state hardware or vendor requirements without checking the code.
