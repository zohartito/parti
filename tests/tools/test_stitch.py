"""PAR-01/PAR-02/PAR-N1: synthetic files only; no Rhino or capture hardware.

Run with: python3 -m unittest discover -s tests/tools -v
Requires ffmpeg on PATH. Temporary artifacts use the caller's TMPDIR.
"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class StitchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="parti-stitch-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        source = Path(__file__).resolve().parents[2] / "tools/grasshopper-deadline-kit/stitch_grasshopper_slider_sequences.sh"
        self.script = self.root / "stitch.sh"
        # Keep the pre-fix reproduction's legacy fixed logs inside this fixture.
        self.script.write_text(source.read_text().replace("/tmp/gh_deadline_", str(self.root / "gh_deadline_")))
        self.env = dict(os.environ, GH_STEMS="shot", GH_LABELS="Shot", GH_GIF_WIDTH="64", GH_CONTACT_TILE="3x1")

    def frames(self, extension):
        folder = self.root / extension
        folder.mkdir()
        subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x36:r=2",
                        "-frames:v", "3", str(folder / ("shot_%03d." + extension))], check=True, capture_output=True)
        return folder

    def run_stitch(self, *args):
        return subprocess.run(["bash", str(self.script), *map(str, args)], cwd=self.root,
                              env=self.env, capture_output=True, text=True, timeout=90)

    def assert_outputs(self, result, out):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        for name in ("grasshopper_slider_loop.mp4", "grasshopper_slider_loop.gif",
                     "grasshopper_slider_loop_labeled.mp4", "grasshopper_slider_loop_labeled.gif",
                     "grasshopper_slider_contact_sheet.png"):
            file = out / name
            self.assertGreater(file.stat().st_size, 0)
            subprocess.run(["ffmpeg", "-v", "error", "-i", str(file), "-f", "null", "-"],
                           check=True, capture_output=True)

    def test_supported_formats(self):
        for extension in ("bmp", "png", "jpg", "jpeg"):
            with self.subTest(extension=extension):
                src = self.frames(extension)
                out = self.root / (extension + "-out")
                self.assert_outputs(self.run_stitch(src, out), out)
                self.assertEqual(b"BM", (out / "_combined_frames/frame_000.bmp").read_bytes()[:2])

    def test_relative_output(self):
        src = self.frames("bmp")
        self.assert_outputs(self.run_stitch(src, "relative-out"), self.root / "relative-out")

    def test_relative_source_default_output(self):
        self.frames("bmp")
        self.assert_outputs(self.run_stitch("bmp"), self.root / "bmp/_stitched")

    def test_failure_reports_ffmpeg_error_and_local_log(self):
        src = self.frames("bmp")
        fake = self.root / "ffmpeg-failure"
        fake.write_text('#!/bin/sh\ncase "$*" in *-filters*) exit 0;; esac\necho "synthetic encoder failure" >&2\nexit 69\n')
        fake.chmod(0o700)
        self.env["FFMPEG"] = str(fake)
        out = self.root / "failed-out"
        result = self.run_stitch(src, out)
        self.assertEqual(69, result.returncode)
        self.assertIn("synthetic encoder failure", result.stderr)
        self.assertIn(str(out / "_logs"), result.stderr)
        self.assertTrue(any("synthetic encoder failure" in p.read_text() for p in (out / "_logs").glob("*.log")))
