"""Offline review handoff through the CLI, using generated disposable repositories."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "agent-review-packet.py"


class ReviewPacketTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "user.name", "Test")
        self.git("remote", "add", "origin", "git@github.com:sntna/jellyfin-plugin-meta-tagger.git")
        (self.root / "source.txt").write_text("before\n")
        self.git("add", "source.txt")
        self.git("commit", "-qm", "base")
        self.base = self.git("rev-parse", "HEAD")
        (self.root / "source.txt").write_text("after\n")
        self.git("commit", "-qam", "head")
        self.head = self.git("rev-parse", "HEAD")
        self.issue = {"number": 39, "title": "Approved complete requirements", "body":
                      "## Objective\nImprove handoff\n## In scope\nLocal packet\n"
                      "## Out of scope\nMerge authority\n## Acceptance criteria\nOffline review\n"
                      "## Blocked by\nNone\n## Priority\nNormal\n", "blockers": []}
        self.approval = hashlib.sha256(json.dumps(
            [self.issue["title"], self.issue["body"], []], sort_keys=True).encode()).hexdigest()
        self.state = self.root / ".git/agent-dispatch"
        (self.state / "runs/39").mkdir(parents=True)
        self.write_json(self.state / "runs/39/ticket.json", self.issue)
        self.write_json(self.state / "approvals.json", {"39": self.approval})
        self.result = self.root / "verification.log"
        self.result.write_text("All checks passed\n")
        self.verification = self.root / "verification.json"
        self.write_json(self.verification, {"head": self.head, "command": "./scripts/build-and-test.sh",
                        "status": "passed", "result": str(self.result), "browser": []})

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, text=True, capture_output=True,
                              check=True).stdout.strip()

    def write_json(self, path, value):
        path.write_text(json.dumps(value))

    def cli(self, *args):
        return subprocess.run(["python3", str(SCRIPT), *map(str, args)], cwd=self.root,
                              text=True, capture_output=True)

    def build(self, *extra):
        return self.cli("build", "--issue", 39, "--base", self.base, "--head", self.head,
                        "--verification", self.verification, *extra)

    def test_saved_approved_issue_and_verification_make_offline_packet(self):
        result = self.build()
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = json.loads(result.stdout)
        manifest_path = Path(summary["manifest"])
        self.assertTrue(manifest_path.is_absolute())
        manifest = json.loads(manifest_path.read_text())
        self.assertEqual(manifest["approval"], self.approval)
        self.assertEqual(manifest["repository"], "sntna/jellyfin-plugin-meta-tagger")
        self.assertEqual(manifest["head"], self.head)
        self.assertEqual(json.loads(Path(manifest["requirements"]).read_text()), self.issue)
        self.assertIn("+after", Path(manifest["diff"]).read_text())
        self.assertEqual(Path(manifest["verification"]["result"]).read_text(), "All checks passed\n")
        self.assertEqual(manifest["reports"], {})
        self.assertEqual(self.cli("check", "--manifest", manifest_path).returncode, 0)

    def test_supervised_snapshot_design_and_browser_evidence_are_available_offline(self):
        ticket_path = self.root / "supervised-ticket.json"
        self.write_json(ticket_path, self.issue)
        design = self.root / "design.html"
        design.write_text("Approved design")
        browser = self.root / "geometry.json"
        browser.write_text('{"desktop_columns": 2}')
        data = json.loads(self.verification.read_text())
        data["browser"] = [str(browser)]
        self.write_json(self.verification, data)
        shutil_state = self.state / "approvals.json"
        shutil_state.unlink()
        result = self.build("--ticket", ticket_path, "--approval", self.approval,
                            "--design", design, "--design-approval", "User approved design in chat")
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = json.loads(Path(json.loads(result.stdout)["manifest"]).read_text())
        self.assertEqual(Path(manifest["design"]["path"]).read_text(), "Approved design")
        self.assertEqual(manifest["design"]["approval"], "User approved design in chat")
        self.assertEqual(Path(manifest["verification"]["browser"][0]).read_text(), browser.read_text())
        browser.unlink()
        design.unlink()
        self.assertTrue(Path(manifest["verification"]["browser"][0]).is_file())

    def test_invalid_requirements_and_evidence_fail_before_packet_creation(self):
        ticket_path = self.root / "ticket.json"
        self.write_json(ticket_path, self.issue)
        cases = [(["--ticket", ticket_path], "--approval"),
                 (["--design", self.result], "--design-approval"),
                 (["--ticket", ticket_path, "--approval", "wrong"], "fingerprint")]
        for extra, diagnostic in cases:
            with self.subTest(diagnostic=diagnostic):
                result = self.build(*extra)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(diagnostic, result.stderr)
        self.assertFalse((self.root / ".git/agent-review/packets").exists())
        data = json.loads(self.verification.read_text())
        for change, diagnostic in [({"head": self.base}, "head"),
                                   ({"result": str(self.root / "absent.log")}, "Missing evidence"),
                                   ({"browser": [str(self.root / "absent.png")]}, "Missing evidence")]:
            with self.subTest(change=change):
                self.write_json(self.verification, dict(data, **change))
                result = self.build()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(diagnostic, result.stderr)
        incomplete = dict(self.issue, body="Short PR description")
        self.write_json(self.state / "runs/39/ticket.json", incomplete)
        self.write_json(self.verification, data)
        self.assertIn("full issue", self.build().stderr)

    def test_changed_comparison_or_scope_marks_packet_obsolete(self):
        for mutation in ("head", "base", "scope", "approval"):
            with self.subTest(mutation=mutation):
                result = self.build()
                self.assertEqual(result.returncode, 0, result.stderr)
                path = Path(json.loads(result.stdout)["manifest"])
                if mutation in {"head", "base"}:
                    checked = self.cli("check", "--manifest", path,
                                       f"--{mutation}", self.base if mutation == "head" else self.head)
                elif mutation == "scope":
                    changed = dict(self.issue, title="Changed approved requirements")
                    self.write_json(self.state / "runs/39/ticket.json", changed)
                    checked = self.cli("check", "--manifest", path)
                    self.write_json(self.state / "runs/39/ticket.json", self.issue)
                else:
                    self.write_json(self.state / "approvals.json", {"39": "changed"})
                    checked = self.cli("check", "--manifest", path)
                    self.write_json(self.state / "approvals.json", {"39": self.approval})
                self.assertNotEqual(checked.returncode, 0)
                self.assertIn("obsolete", checked.stderr)
                self.assertEqual(json.loads(path.read_text())["status"], "obsolete")

    def test_reports_are_copied_separately_and_new_head_does_not_reuse_them(self):
        first = Path(json.loads(self.build().stdout)["manifest"])
        report = self.root / "review.md"
        report.write_text("Checked complete diff")
        record = lambda axis, reviewer: self.cli("record", "--manifest", first, "--axis", axis,
                                                "--reviewer", reviewer, "--report", report)
        self.assertEqual(record("standards", "reviewer-a").returncode, 0)
        repeated = record("spec", "reviewer-a")
        self.assertNotEqual(repeated.returncode, 0)
        self.assertIn("different", repeated.stderr)
        self.assertEqual(record("spec", "reviewer-b").returncode, 0)
        manifest = json.loads(first.read_text())
        self.assertNotEqual(manifest["reports"]["standards"]["path"], manifest["reports"]["spec"]["path"])
        report.unlink()
        self.assertEqual(Path(manifest["reports"]["spec"]["path"]).read_text(), "Checked complete diff")
        (self.root / "source.txt").write_text("new head\n")
        self.git("commit", "-qam", "repair")
        self.head = self.git("rev-parse", "HEAD")
        data = json.loads(self.verification.read_text())
        self.write_json(self.verification, dict(data, head=self.head))
        second = self.build()
        self.assertEqual(second.returncode, 0, second.stderr)
        manifest = json.loads(Path(json.loads(second.stdout)["manifest"]).read_text())
        self.assertEqual(manifest["reports"], {})
        self.assertEqual(json.loads(first.read_text())["status"], "obsolete")
        self.assertNotEqual(record("standards", "reviewer-c").returncode, 0)

    def test_moving_ref_is_checked_without_network_access(self):
        result = self.cli("build", "--issue", 39, "--base", self.base, "--head", "HEAD",
                          "--verification", self.verification)
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest_path = Path(json.loads(result.stdout)["manifest"])
        (self.root / "source.txt").write_text("changed branch\n")
        self.git("commit", "-qam", "changed")
        result = self.cli("check", "--manifest", manifest_path)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("obsolete", result.stderr)
        self.assertEqual(json.loads(manifest_path.read_text())["status"], "obsolete")

    def test_check_rejects_missing_or_changed_copied_evidence(self):
        manifest_path = Path(json.loads(self.build().stdout)["manifest"])
        manifest = json.loads(manifest_path.read_text())
        for path in (manifest["verification"]["metadata"], manifest["verification"]["result"], manifest["diff"]):
            with self.subTest(path=path):
                copied = Path(path)
                original = copied.read_bytes()
                copied.write_text("Changed evidence")
                result = self.cli("check", "--manifest", manifest_path)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("changed", result.stderr)
                copied.write_bytes(original)
        manifest["verification"]["result"] = str(self.root / "missing.log")
        self.write_json(manifest_path, manifest)
        result = self.cli("check", "--manifest", manifest_path)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Missing evidence", result.stderr)


if __name__ == "__main__":
    unittest.main()
