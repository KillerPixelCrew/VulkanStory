#!/usr/bin/env python3
"""Offline contract tests for the pinned decoder acquisition step."""

import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile


SCRIPT = Path(__file__).with_name("acquire-delta-decoder.py")
VERSION = "3.2.0"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def add_tar_member(archive, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    archive.addfile(info, io.BytesIO(data))


class AcquireDecoderTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.assets = self.root / "assets"
        self.assets.mkdir()
        self.binary = b"synthetic-xdelta-executable"
        self.client = self.root / "vs_install_win-x64_1.22.7.exe"
        self.client.write_bytes(b"synthetic-client-archive")

        source = self.assets / f"xdelta3-{VERSION}.tar.gz"
        with tarfile.open(source, "w:gz") as archive:
            add_tar_member(archive, f"xdelta3-{VERSION}/LICENSE", b"Test license\n")
        windows = self.assets / f"xdelta3-{VERSION}-windows-x86_64.zip"
        with zipfile.ZipFile(windows, "w") as archive:
            archive.writestr(f"xdelta3-{VERSION}-windows-x86_64/xdelta3.exe", self.binary)
        linux = self.assets / f"xdelta3-{VERSION}-linux-x86_64.tar.gz"
        with tarfile.open(linux, "w:gz") as archive:
            add_tar_member(archive, f"xdelta3-{VERSION}-linux-x86_64/xdelta3", self.binary)
        self.lock = {
            "clients": {
                "win-x64": {"archive": self.client.name, "sha256": digest(self.client)}
            },
            "decoder": {
                "version": VERSION,
                "repository": "jmacd/xdelta",
                "sourceArchive": {"name": source.name, "sha256": digest(source)},
                "win-x64": {"name": windows.name, "sha256": digest(windows),
                            "binarySha256": hashlib.sha256(self.binary).hexdigest()},
                "linux-x64": {"name": linux.name, "sha256": digest(linux),
                              "binarySha256": hashlib.sha256(self.binary).hexdigest()}
            }
        }
        self.manifest = self.root / "release-inputs.json"
        self.write_lock()

    def write_lock(self):
        self.manifest.write_text(json.dumps(self.lock), encoding="utf-8")

    def run_acquire(self, rid="win-x64", *extra):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--rid", rid,
             "--output", str(self.root / "bundle"), "--asset-dir", str(self.assets),
             "--manifest", str(self.manifest), *extra],
            capture_output=True, text=True)

    def test_verified_windows_and_linux_bundles(self):
        result = self.run_acquire("win-x64", "--client-archive", str(self.client))
        self.assertEqual(result.returncode, 0, result.stderr)
        bundle = self.root / "bundle"
        self.assertEqual((bundle / "xdelta3.exe").read_bytes(), self.binary)
        self.assertEqual((bundle / "decoder.json").exists(), True)
        self.assertEqual((bundle / "LICENSE-xdelta.txt").read_text(), "Test license\n")
        linux_output = self.root / "linux-bundle"
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--rid", "linux-x64", "--output", str(linux_output),
             "--asset-dir", str(self.assets), "--manifest", str(self.manifest)],
            capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((linux_output / "xdelta3").read_bytes(), self.binary)

    def test_rejects_tampered_archive_without_publishing(self):
        with (self.assets / self.lock["decoder"]["win-x64"]["name"]).open("ab") as asset:
            asset.write(b"tampered")
        result = self.run_acquire()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "bundle").exists())

    def test_rejects_wrong_client_archive_without_publishing(self):
        self.client.write_bytes(b"different-client")
        result = self.run_acquire("win-x64", "--client-archive", str(self.client))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "bundle").exists())


if __name__ == "__main__":
    unittest.main()
