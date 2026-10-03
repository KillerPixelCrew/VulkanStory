"""Create a consistent local snapshot, including committed WAL content, without copying a live database file."""
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
