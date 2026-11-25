"""PAR-27: immutable action revisions and least-privilege workflow defaults."""
from pathlib import Path
import re
import unittest


WORKFLOWS = Path(__file__).resolve().parents[2] / ".github/workflows"


class WorkflowSecurityTests(unittest.TestCase):
    def test_actions_use_full_commit_hashes(self):
        for path in WORKFLOWS.glob("*.yml"):
            for action in re.findall(r"uses:\s*(\S+)", path.read_text()):
                with self.subTest(workflow=path.name, action=action):
                    self.assertRegex(action, r"^[\w.-]+/[\w.-]+@[a-f0-9]{40}$")

    def test_read_only_defaults_and_release_only_write_permission(self):
        for path in WORKFLOWS.glob("*.yml"):
            text = path.read_text()
            default = re.search(r"^permissions:\n((?:[ \t]+.*\n)+)", text, re.MULTILINE)
            with self.subTest(workflow=path.name):
                self.assertIsNotNone(default)
                self.assertIn("contents: read", default.group(1))
                self.assertNotIn("write", default.group(1))
                writes = re.findall(r"^\s+\w[\w-]*: write$", text, re.MULTILINE)
                self.assertEqual(1 if path.name == "release.yml" else 0, len(writes))
                if path.name == "release.yml":
                    self.assertRegex(text, r"draft-release:\n(?:    .*\n)*?    permissions:\n      contents: write")
