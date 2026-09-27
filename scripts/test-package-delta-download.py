#!/usr/bin/env python3
"""Small archive-contract tests for package-delta-download.py."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


SCRIPT = Path(__file__).with_name("package-delta-download.py")
NAME = "Optimum-v0.3.17-VS1.22.7-linux-x64-Delta.zip"


class PackageDeltaDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.release = self.root / "release"
        self.release.mkdir()
        self.output = self.root / NAME
        files = {
            "delta-pack/manifest.json": json.dumps({
                "format": "optimum-vcdiff-1", "gameVersion": "1.22.7",
                "optimumVersion": "0.3.17", "rid": "linux-x64", "files": []}),
            "runtime-payload/Optimum": "launcher",
            "delta-decoder/decoder.json": "decoder",
            "delta-decoder/xdelta3": "binary",
            "delta-uninstaller": "remove",
        }
        for name, content in files.items():
            path = self.release / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        payload = self.release / "runtime-payload/Optimum"
        removal = self.release / "delta-uninstaller"
        self.descriptor = {
            "gameVersion": "1.22.7", "optimumVersion": "0.3.17", "rid": "linux-x64",
            "payloadFiles": [{"path": "Optimum", "size": payload.stat().st_size,
                              "sha256": hashlib.sha256(payload.read_bytes()).hexdigest()}],
            "uninstaller": {"path": "delta-uninstaller", "size": removal.stat().st_size,
                            "sha256": hashlib.sha256(removal.read_bytes()).hexdigest()},
        }
        self.write_descriptor()

    def write_descriptor(self):
        (self.release / "delta-release.json").write_text(
            json.dumps(self.descriptor), encoding="utf-8")

    def run_packager(self, *extra):
        return subprocess.run([sys.executable, str(SCRIPT), "--release", str(self.release),
                               "--output", str(self.output), *extra],
                              capture_output=True, text=True)

    def test_archive_contains_only_delta_inputs_with_executable_modes(self):
        result = self.run_packager()
        self.assertEqual(0, result.returncode, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            names = set(archive.namelist())
            self.assertEqual({"delta-release.json", "delta-pack/manifest.json",
                              "runtime-payload/Optimum", "delta-decoder/decoder.json",
                              "delta-decoder/xdelta3", "delta-uninstaller"}, names)
            self.assertEqual(0o755, archive.getinfo("runtime-payload/Optimum").external_attr >> 16 & 0o777)
            self.assertEqual(0o644, archive.getinfo("delta-release.json").external_attr >> 16 & 0o777)

    def test_unlisted_payload_file_prevents_archive(self):
        (self.release / "runtime-payload/extra.dll").write_text("extra", encoding="utf-8")
        result = self.run_packager()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.output.exists())

    def test_local_proof_requires_explicit_switch(self):
        (self.release / "runtime-payload/LOCAL-PROOF-ONLY.txt").write_text("proof", encoding="utf-8")
        result = self.run_packager()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
