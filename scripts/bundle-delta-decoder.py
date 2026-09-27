#!/usr/bin/env python3
"""Stage a locally acquired xdelta 3.2.0 binary and its license for dotnet publish.

No download or installation. Pass the resulting directory as DeltaDecoderDirectory.
The release pipeline must authenticate its source binary before invoking this tool.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--executable", required=True, type=Path)
parser.add_argument("--license", required=True, type=Path)
parser.add_argument("--rid", required=True, choices=("win-x64", "linux-x64"))
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
if args.output.exists():
    parser.error("Output already exists")
if not args.license.is_file() or not args.license.read_text(encoding="utf-8").strip():
    parser.error("The upstream license is required")
args.output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=args.output.parent, prefix="decoder-stage-") as temporary:
    stage = Path(temporary) / "bundle"
    stage.mkdir()
    target = stage / ("xdelta3.exe" if args.rid == "win-x64" else "xdelta3")
    shutil.copyfile(args.executable, target)
    target.chmod(0o755)
    shutil.copyfile(args.license, stage / "LICENSE-xdelta.txt")
    with target.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    (stage / "decoder.json").write_text(json.dumps({"version": "3.2.0", "rid": args.rid,
                                                  "sha256": checksum}, indent=2) + "\n", encoding="utf-8")
    stage.rename(args.output)
