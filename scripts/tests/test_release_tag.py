from __future__ import annotations

from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ReleaseTagTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in (
            "scripts/release_tag.py", "scripts/package_release.py",
            "Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj",
            "Jellyfin.Plugin.MetaTagger/Plugin.cs", "Jellyfin.Plugin.MetaTagger/manifest.json",
        ):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        (self.root / ".gitignore").write_text("__pycache__/\n")
        self.git("init", "-b", "main")
        self.git("config", "user.name", "sntna")
        self.git("config", "user.email", "1961713+sntna@users.noreply.github.com")
        self.git("add", ".")
        self.git("-c", "commit.gpgsign=false", "commit", "-m", "Initial release")
        self.git("config", "tag.gpgsign", "false")

    def git(self, *args: str) -> str:
        return subprocess.check_output(["git", *args], cwd=self.root, text=True, stderr=subprocess.DEVNULL).strip()

    def run_tag(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([sys.executable, "scripts/release_tag.py", *args], cwd=self.root, text=True, capture_output=True)

    def test_check_does_not_create_tag_and_create_is_idempotent(self) -> None:
        result = self.run_tag()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("v0.1.0", result.stdout.strip())
        self.assertEqual("", self.git("tag", "--list"))
        result = self.run_tag("--create")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.git("rev-parse", "v0.1.0^{}"))
        self.assertEqual("tag", self.git("cat-file", "-t", "v0.1.0"))
        original = self.git("rev-parse", "v0.1.0")
        self.assertEqual(0, self.run_tag("--create").returncode)
        self.assertEqual(original, self.git("rev-parse", "v0.1.0"))

    def test_rejects_dirty_tree(self) -> None:
        (self.root / "uncommitted.txt").write_text("pending change")
        result = self.run_tag("--create")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Commit all changes", result.stderr)
        self.assertEqual("", self.git("tag", "--list"))

    def test_never_moves_existing_tag(self) -> None:
        self.assertEqual(0, self.run_tag("--create").returncode)
        original = self.git("rev-parse", "v0.1.0")
        self.git("-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Later change")
        result = self.run_tag("--create")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("different commit", result.stderr)
        self.assertEqual(original, self.git("rev-parse", "v0.1.0"))

    def test_rejects_inconsistent_version_fields(self) -> None:
        project = self.root / "Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj"
        project.write_text(project.read_text().replace("<PackageVersion>0.1.0</PackageVersion>", "<PackageVersion>0.2.0</PackageVersion>"))
        self.git("add", ".")
        self.git("-c", "commit.gpgsign=false", "commit", "-m", "Inconsistent version")
        result = self.run_tag("--create")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("versions do not match", result.stderr)
        self.assertEqual("", self.git("tag", "--list"))


if __name__ == "__main__":
    unittest.main()
