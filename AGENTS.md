# Agent workflow

- A turn is either implementation or validation. Do not alternate between coding and testing in the same turn. During an implementation turn, inspect and edit code without launching builds, tests, probes, packages, or game runs.
- During a validation turn, plan one bounded batch of relevant build, test, package, and live checks. Run the batch once, record the complete result, and stop. Never start a second validation batch in that turn.
- If validation fails, diagnose it from the existing logs and source code and report the failure. Do not rerun the same or a nearly identical test, repeatedly repackage, or launch another full suite to check a speculative change. Make a concrete fix in a later implementation turn before validating again.
- Do not automatically run the full suite on a goal continuation. Report unresolved failures honestly and keep the milestone open instead of entering a test loop.
