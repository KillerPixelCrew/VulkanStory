# Workflow policy - SUPERSEDED 2026-09-16

The owner stopped workflow-based work: "The Workflow approach does not work for me. I cant see whats happening."
Work happens directly in the session, sequentially (AGENTS.md rule 8). This file is kept only so old references resolve.

---

---
name: workflow-policy
description: Model, effort and parallelism rules for Workflow (ultracode) runs in Optimum. Use before writing any workflow script or launching any subagent.
---

# Workflow policy (user rules, 2026-09-10)

| Role | model | effort | notes |
|---|---|---|---|
| Map / search / inventory | sonnet | high or xhigh | cheap; lower effort gives untrustworthy maps |
| Implementation stage | opus | medium | never high; P3/P4 at high took 30-40 min per stage |
| Integration / merge stage | opus | medium | merges worktree branches, resolves conflicts, runs finish sequence |
| Review stage | opus | medium | adversarial, fixes defects with regression tests |
| Fable (main session) | - | low | high only for a hard bug; never inherited by agents |

Shape:
1. `phase('Map')`: one sonnet agent, read-only, returns file:line touch points.
2. `parallel(stages.map(s => () => agent(..., {isolation: 'worktree', model: 'opus', effort: 'medium'})))`
   for every independent stage. Each stage commits on its worktree branch with `wip(<topic>): ...`
   and returns the branch name and commit.
3. `phase('Integrate')`: one opus agent merges every branch into the feature branch, resolves
   conflicts (Program.cs transplant list, ClientPlatformWindows, shader includes are the usual
   ones), reruns extract/check-patches, build, both test suites, `make patch-il` (Cecil patch, no deploy) and the fork-API drift check (AGENTS.md rule 19), commits.
4. `phase('Review')`: one opus agent, then Fable verifies in game (run-optimum skill).

Prompt rules for every stage: read CLAUDE.md and the plan section first; sources of truth table;
never stash, never launch the game, never `make deploy`, never `pkill -f` with the process name in
the same command; mandatory tests (Optimum.Tests coverage + GPU readback in
Optimum.Render.Vulkan.Tests); return structured data via `schema`.

## No map stage by default (2026-09-16, owner)

Map stages were opening every workflow and costing five figures of tokens each to rediscover the tree, then
being thrown away with the run. Two rules replace that:

1. **Do not add a map stage** unless the question cannot be answered from the code: measured behaviour, vendor
   documentation, or a tree the repository does not contain. For "where is X, what state does it set, what
   writes this attachment", the implementation stage greps and reads - that is cheaper than a stage and it
   cannot go stale.
2. **Every implementation stage documents the seams it touches**, per "Documentation that makes map stages
   unnecessary" in docs/vulkan-native-render-systems.md: what it draws, where the other side is, target and
   slots, non-obvious state, and the test that pins it. State this requirement in the stage prompt. A stage
   that adds a seam without the comment is incomplete.

`scripts/dev/harvest-maps.py` recovers the map output of past runs from the workflow journals if one is
genuinely needed again.
