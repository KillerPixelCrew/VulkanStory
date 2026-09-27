#!/usr/bin/env python3
"""Merge Optimum strings into an exact vanilla language file without rewriting it."""

import json
from pathlib import Path


def merge(original: Path, english: Path, localized: Path) -> bytes:
    # The vanilla files contain keys differing only by case. Preserve their
    # spelling, order and UTF-8 bytes instead of round-tripping the full JSON.
    base = original.read_text(encoding="utf-8-sig").rstrip()
    if not base.endswith("}"):
        raise ValueError(f"Invalid vanilla language file: {original}")
    fallback = json.loads(english.read_text(encoding="utf-8"))
    translations = json.loads(localized.read_text(encoding="utf-8"))
    keys = [key for key in fallback if key.startswith("optimum-")]
    if not keys:
        raise ValueError(f"Missing Optimum strings: {english}")
    additions = ["\t" + json.dumps(key, ensure_ascii=False) + ": " +
                 json.dumps(translations.get(key, fallback[key]), ensure_ascii=False)
                 for key in keys]
    prefix = base[:-1].rstrip()
    if not prefix.endswith("{") and not prefix.endswith(","):
        prefix += ","
    merged = prefix + "\r\n" + ",\r\n".join(additions) + "\r\n}\r\n"
    json.loads(merged)
    return merged.encode("utf-8")
