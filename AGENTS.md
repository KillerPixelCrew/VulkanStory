# Agent workflow

- User development workflow (2026-10-01): use the migrated headless harness with an isolated snapshot of foggy village story for renderer/provider checks. Do not deploy, open, focus or close the user game to run routine checks. Keep the implementation/validation turn boundary.

- User world-validation preference (2026-10-01): use the created foggy village story save for world loading and representative rendering. Do not use development superflat.

- Current user priority (2026-09-30): defer writing or expanding tests until the renderer, SDL and provider integration is finished. Prioritize implementation and runtime wiring; do not insert routine test batches between each source increment. Preserve recorded evidence and report unverified work honestly.

- A turn is either implementation or validation. Do not alternate between coding and testing in the same turn. During an implementation turn, inspect and edit code without launching builds, tests, probes, packages, or game runs.
- During a validation turn, plan one bounded batch of relevant build, test, package, and live checks. Run the batch once, record the complete result, and stop. Never start a second validation batch in that turn.
- If validation fails, diagnose it from the existing logs and source code and report the failure. Do not rerun the same or a nearly identical test, repeatedly repackage, or launch another full suite to check a speculative change. Make a concrete fix in a later implementation turn before validating again.
- Do not automatically run the full suite on a goal continuation. Report unresolved failures honestly and keep the milestone open instead of entering a test loop.

# Architecture boundaries

- Read `docs/ROADMAP.md`, `docs/architecture.md`, `docs/bootstrap-and-installation.md`, and `docs/porting-plan.md` before implementation.
- This is a fresh VulkanStory mod. Do not import the Optimum launcher, donor/transplant/delta pipeline, modified game assemblies, or unrelated optimization systems.
- Compile game-facing projects against official game assemblies. Keep references private to the game integration and mod projects; do not ship those assemblies.
- Keep game internals and Harmony out of renderer, SDL, and provider implementations. Bootstrap code must not initialize graphics while the OS loader lock is held.
- Additional fields and state belong to VulkanStory objects. Do not depend on injected members, unsealed game types, or added virtual slots.
- Prefer direct source migration for working renderer, shader, SDL, and provider code. Limit changes to names, dependency boundaries, and explicit host/state interfaces. Do not combine the port with optional rendering algorithm or resource-layout refactors.
- Treat bootstrap timing, proxy coexistence, game-version coverage, and each vendor/platform validation gap as open until supported by recorded evidence.
- Preserve attribution and file provenance when migrating code. A namespace or project rename does not change a source file's license.


# Roadmap maintenance

- `docs/ROADMAP.md` is authoritative for current feature status and remaining work. Update the relevant task ID/status/evidence and current source/package identity; do not append overlapping chronological progress sections to the active roadmap or porting plan.
- Keep detailed implementation/validation chronology in linked records. The 2026-10-01 history snapshots retain older plans and baseline ideas; historical claims do not supersede current evidence.
- Keep implementation gaps, known defects, scoped verification gaps, unavailable-hardware gates and subsequent compatibility/platform work distinct. Test writing stays deferred until integration is finished.