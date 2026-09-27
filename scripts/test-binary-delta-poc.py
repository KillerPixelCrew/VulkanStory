"""Run with OPTIMUM_XDELTA pointing to xdelta3. Uses synthetic data only."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("binary-delta-poc.py")
spec = importlib.util.spec_from_file_location("delta_poc", SCRIPT)
poc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(poc)


@unittest.skipUnless(os.environ.get("OPTIMUM_XDELTA"), "Set OPTIMUM_XDELTA to run integration tests")
class DeltaTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.original, self.patched, self.pack = [self.root / name for name in ("original", "patched", "pack")]
        for relative in poc.TARGETS:
            for folder, content in ((self.original, b"original-data" * 1000),
                                    (self.patched, b"original-data" * 500 + b"changed" + b"original-data" * 500)):
                path = folder / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
        self.invoke("build", "--patched", self.patched, "--output", self.pack,
                    "--game-version", "1.22.7", "--optimum-version", "0.3.17", "--rid", "linux-x64")

    def invoke(self, command, *args, success=True):
        result = subprocess.run([sys.executable, str(SCRIPT), command, "--xdelta", os.environ["OPTIMUM_XDELTA"],
                                 "--original", str(self.original), *map(str, args)], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stderr)
        return result

    def apply(self, success=True):
        output = self.root / "restored"
        self.invoke("apply", "--pack", self.pack, "--output", output, success=success)
        self.assertEqual(output.exists(), success)
        return output

    def edit_manifest(self, edit):
        path = self.pack / "manifest.json"
        manifest = json.loads(path.read_text())
        edit(manifest)
        path.write_text(json.dumps(manifest))

    def test_round_trip(self):
        output = self.apply()
        for relative in poc.TARGETS:
            self.assertEqual((self.patched / relative).read_bytes(), (output / relative).read_bytes())
            self.assertEqual((self.original / relative).read_bytes(), b"original-data" * 1000)

    def test_wrong_original(self):
        (self.original / poc.TARGETS[0]).write_bytes(b"wrong version")
        self.apply(False)

    def test_corrupt_delta(self):
        with (self.pack / (poc.TARGETS[0] + ".vcdiff")).open("ab") as stream:
            stream.write(b"corruption")
        self.apply(False)

    def test_wrong_output_hash(self):
        self.edit_manifest(lambda m: m["files"][-1].update(outputSha256="0" * 64))
        self.apply(False)

    def test_path_traversal(self):
        self.edit_manifest(lambda m: m["files"][0].update(delta="../outside.vcdiff"))
        self.apply(False)

    def test_duplicate_target(self):
        self.edit_manifest(lambda m: m["files"].__setitem__(0, m["files"][1]))
        self.apply(False)

    def test_existing_output_untouched(self):
        output = self.root / "restored"
        output.mkdir()
        marker = output / "keep.txt"
        marker.write_text("keep")
        self.invoke("apply", "--pack", self.pack, "--output", output, success=False)
        self.assertEqual(marker.read_text(), "keep")

    def test_added_shader_uses_an_official_source(self):
        source = self.original / "assets/game/shaders/standard.vsh"
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_text("official shader\n")
        overlays = self.root / "overlays"
        shader = overlays / "shaders/taa-resolve.vsh"
        shader.parent.mkdir(parents=True, exist_ok=True)
        shader.write_text("optimum shader\n")
        (overlays / "shaderincludes").mkdir()
        second = self.root / "second-pack"
        self.invoke("build", "--patched", self.patched, "--output", second,
                    "--game-version", "1.22.7", "--optimum-version", "0.3.17",
                    "--rid", "linux-x64", "--asset-overlays", overlays)
        manifest = json.loads((second / "manifest.json").read_text())
        self.assertEqual("assets/game/shaders/standard.vsh", manifest["files"][-1]["sourcePath"])
        self.invoke("apply", "--pack", second, "--output", self.root / "shader-restored")
        self.assertEqual("optimum shader\n", (self.root / "shader-restored/assets/game/shaders/taa-resolve.vsh").read_text())


if __name__ == "__main__":
    unittest.main()
