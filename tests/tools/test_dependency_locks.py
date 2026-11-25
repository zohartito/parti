"""PAR-26: verify lock coverage and fail-closed restore/install configuration."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


class DependencyLockTests(unittest.TestCase):
    def test_nuget_projects_have_enforced_hash_locks(self):
        props = ET.parse(ROOT / "Directory.Build.props").getroot()
        self.assertEqual("true", props.findtext(".//RestorePackagesWithLockFile"))
        self.assertEqual("true", props.findtext(".//RestoreLockedMode"))
        for project in ROOT.rglob("*.csproj"):
            if "obj" in project.parts or "bin" in project.parts:
                continue
            with self.subTest(project=project.name):
                lock = project.with_name("packages.lock.json")
                self.assertTrue(lock.is_file(), str(lock))
                data = json.loads(lock.read_text())
                for framework in data["dependencies"].values():
                    for package in framework.values():
                        if package["type"].lower() != "project":
                            self.assertTrue(package.get("contentHash"))

    def test_nuget_locked_restore_rejects_dependency_drift(self):
        lock = ROOT / "src/Parti.Core/packages.lock.json"
        self.assertTrue(lock.is_file(), "No reviewed lock exists to enforce")
        with tempfile.TemporaryDirectory(prefix="parti-lock-") as temporary:
            folder = Path(temporary)
            (folder / "packages.lock.json").write_bytes(lock.read_bytes())
            (folder / "empty-feed").mkdir()
            (folder / "Probe.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><Import Project="' + str(ROOT / "Directory.Build.props") + '"/>'
                '<ItemGroup><PackageReference Include="Newtonsoft.Json" Version="[13.0.2]"/></ItemGroup></Project>')
            result = subprocess.run(["dotnet", "restore", str(folder / "Probe.csproj"), "--source", str(folder / "empty-feed"),
                                     "-p:NuGetAudit=false", "--nologo"], capture_output=True, text=True, timeout=60)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("NU1004", result.stdout + result.stderr)

    def test_python_runtime_and_build_lock_is_hash_enforced(self):
        package = ROOT / "mcp/parti_mcp"
        lock = package / "requirements.lock"
        self.assertTrue(lock.is_file(), "Python runtime and build dependencies are unlocked")
        text = lock.read_text().replace("\\\n", " ")
        requirements = [line for line in text.splitlines() if line and not line.startswith("#")]
        self.assertTrue(any(line.startswith("mcp==") for line in requirements))
        self.assertTrue(any(line.startswith("hatchling==") for line in requirements))
        self.assertTrue(any(line.startswith("editables==") for line in requirements))
        for line in requirements:
            self.assertRegex(line, r"^[\w.-]+==[^ ]+")
            self.assertRegex(line, r"--hash=sha256:[a-f0-9]{64}")
        workflow = (ROOT / ".github/workflows/build.yml").read_text()
        self.assertIn("--require-hashes", workflow)
        self.assertIn("--only-binary=:all:", workflow)
        self.assertIn("--no-deps --no-build-isolation", workflow)
