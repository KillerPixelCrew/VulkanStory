#!/usr/bin/env python3
"""Collect the read-only map stages of past workflow runs into docs/vulkan-render-map.md.

Every workflow that starts with a map stage pays a five-figure token bill to rediscover where things
are. The results were already written to the per-run journals and then thrown away. This pulls them
back out, keeps the newest result per map label, and writes one document a later stage can read
instead of mapping the tree again.

Usage:
    python3 scripts/dev/harvest-maps.py [--journals <dir>] [--out docs/vulkan-render-map.md]
                                        [--label-prefix map:] [--max-chars 60000]

The journals live beside the session transcripts, not in the repository, so this is a one-way import:
run it after a workflow whose map stage found something worth keeping, then review the diff. A section
that disagrees with the tree is worse than no section - correct it by hand rather than trusting age.
"""

import argparse
import json
import os
import time

DEFAULT_JOURNALS = os.path.expanduser(
    "~/.claude/projects/-home-n1ght-Projekte-Optimum/"
    "7b680a84-9a6a-436d-a472-1a2eb79eb45f/subagents/workflows"
)

HEADER = """# Vulkan render map (living document)

Where things are on the Vulkan path, so a workflow stage does not have to rediscover them.

**Read this before writing a map stage.** Map only what this file does not already answer, and fold
anything new back in: run `python3 scripts/dev/harvest-maps.py` after a workflow whose map stage found
something, then review the diff.

Each section is one map agent's own output, unedited, with the date it was produced. Age matters: the
tree moves, and a section that disagrees with it is worse than no section. Verify a file:line before
you rely on it; correct the section when you find it stale.

Design rationale lives in `docs/vulkan-native-render-systems.md`. Status lives in
`docs/vulkan-branch-progress.md`. This file is only "where is it".
"""


def collect(journal_dir, prefix):
    """Newest result per map label across every run, joined agentId -> label."""
    best = {}
    for run in sorted(os.listdir(journal_dir)):
        path = os.path.join(journal_dir, run, "journal.jsonl")
        if not os.path.exists(path):
            continue
        labels, results = {}, []
        for line in open(path, encoding="utf-8"):
            try:
                rec = json.loads(line)
            except ValueError:
                continue
            kind = rec.get("type")
            agent = rec.get("agentId")
            if kind == "started" and agent and rec.get("label"):
                labels[agent] = rec["label"]
            elif kind == "result" and agent:
                results.append((agent, rec.get("result")))
        for agent, value in results:
            label = labels.get(agent)
            if not label or not label.startswith(prefix):
                continue
            if isinstance(value, (dict, list)):
                value = json.dumps(value, indent=2)
            if not isinstance(value, str) or len(value) < 500:
                continue
            transcript = os.path.join(journal_dir, run, f"agent-{agent}.jsonl")
            when = os.path.getmtime(transcript) if os.path.exists(transcript) else 0
            if label not in best or when > best[label][0]:
                best[label] = (when, run, value)
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--journals", default=DEFAULT_JOURNALS)
    ap.add_argument("--out", default="docs/vulkan-render-map.md")
    ap.add_argument("--label-prefix", default="map:")
    ap.add_argument("--max-chars", type=int, default=60000)
    args = ap.parse_args()

    if not os.path.isdir(args.journals):
        raise SystemExit(f"no journal directory at {args.journals}")

    best = collect(args.journals, args.label_prefix)
    if not best:
        raise SystemExit("no map results found")

    order = sorted(best.items(), key=lambda kv: -kv[1][0])
    out = [HEADER, "\n## What is in here\n",
           "| map | produced | size | source run |", "|---|---|---|---|"]
    for label, (when, run, value) in order:
        day = time.strftime("%Y-%m-%d", time.localtime(when)) if when else "unknown"
        out.append(f"| [{label}](#{label.replace(':', '').replace('_', '')}) | {day} | "
                   f"{len(value) // 1000}k | `{run}` |")
    out.append("")

    for label, (when, run, value) in order:
        day = time.strftime("%Y-%m-%d", time.localtime(when)) if when else "unknown"
        body = value
        if len(body) > args.max_chars:
            body = body[:args.max_chars] + (
                f"\n\n*[truncated at {args.max_chars} characters; the full result is in "
                f"the run's journal, `{run}`]*\n")
        out.append(f"---\n\n## {label}\n\nProduced {day} by run `{run}`, unedited.\n\n{body}\n")

    with open(args.out, "w", encoding="utf-8") as handle:
        handle.write("\n".join(out))
    print(f"wrote {args.out}: {len(order)} maps, {os.path.getsize(args.out)} bytes")
    for label, (when, run, value) in order:
        print(f"  {label:34} {len(value) // 1000:>4}k  {run}")


if __name__ == "__main__":
    main()
