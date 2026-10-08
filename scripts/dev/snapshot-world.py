"""Create a consistent SQLite world snapshot including committed WAL content.

Usage: snapshot-world.py <source.vcdbs> <fresh-destination.vcdbs>
The source must be an existing file and the destination must not exist. Parent
directories are created before SQLite backup copies the read-only source in
256-page batches. Source settings/save files are not modified by this script.
Bad inputs exit with a message; argument, filesystem, and SQLite failures
propagate. An interrupted backup may leave a destination for inspection.
"""
import pathlib
import sqlite3
import sys

source, destination = map(pathlib.Path, sys.argv[1:3])
if not source.is_file() or destination.exists():
    raise SystemExit("Require an existing source and a fresh destination.")
destination.parent.mkdir(parents=True, exist_ok=True)
with sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True, timeout=30) as original:
    with sqlite3.connect(destination) as snapshot:
        original.backup(snapshot, pages=256, sleep=0.05)
