"""Queue and execution behavior at the dispatcher boundary, without live GitHub."""

import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("agent_dispatch", Path(__file__).parents[1] / "agent-dispatch.py")
dispatcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(dispatcher)


def ticket():
    return {"number": 12, "title": "fix: preserve manual tags", "state": "open", "assignees": [],
        "labels": [{"name": "agent:ready"}], "html_url": f"https://github.com/{dispatcher.REPO}/issues/12",
        "created_at": "2026-01-01T00:00:00Z", "blockers": [], "priority": 1,
        "body": "## Objective\nPreserve tags\n## In scope\nTag cleanup\n## Out of scope\nUI\n"
                "## Acceptance criteria\nRegression test\n## Blocked by\nNone\n## Priority\nNormal"}


class QueueTests(unittest.TestCase):
    def setUp(self):
        self.issue = ticket()
        self.approvals = {"12": dispatcher.fingerprint(self.issue)}

    def test_only_approved_unchanged_unclaimed_ticket_is_eligible(self):
        self.assertTrue(dispatcher.eligible(self.issue, self.approvals, [], {}))
        self.assertFalse(dispatcher.eligible(self.issue, {}, [], {}))
        for field, value in [("title", "Changed scope"), ("body", "Changed body"), ("blockers", [4])]:
            changed = copy.deepcopy(self.issue)
            changed[field] = value
            self.assertFalse(dispatcher.eligible(changed, self.approvals, [], {}))
        for status in ("running", "blocked", "review"):
            self.assertFalse(dispatcher.eligible(self.issue, self.approvals, [], {"12": {"status": status}}))

    def test_triage_and_execution_states_cannot_be_picked_up(self):
        for label in dispatcher.EXCLUDED:
            issue = copy.deepcopy(self.issue)
            issue["labels"].append({"name": label})
            self.assertFalse(dispatcher.eligible(issue, self.approvals, [], {}), label)
        self.issue["assignees"] = [{"login": "someone"}]
        self.assertFalse(dispatcher.eligible(self.issue, self.approvals, [], {}))

    def test_existing_branch_pr_or_closing_pr_prevents_duplicate(self):
        for pr in [{"headRefName": "codex/issue-12", "closingIssuesReferences": []},
                   {"headRefName": "manual-fix", "closingIssuesReferences": [{"url": self.issue["html_url"]}]}]:
            self.assertFalse(dispatcher.eligible(self.issue, self.approvals, [pr], {}))

    def test_native_and_text_blockers_are_combined(self):
        self.issue["body"] = self.issue["body"].replace("\nNone\n", "\n- #3\n")
        with patch.object(dispatcher, "api", return_value=self.issue), patch.object(dispatcher, "flatten",
                return_value=[{"number": 4, "url": f"https://api.github.com/repos/{dispatcher.REPO}/issues/4"}]):
            self.assertEqual(dispatcher.snapshot(12)["blockers"], [3, 4])

    def test_unknown_dependency_syntax_fails_closed(self):
        self.issue["body"] = self.issue["body"].replace("\nNone\n", "\nother/repo#4\n")
        with patch.object(dispatcher, "api", return_value=self.issue), patch.object(dispatcher, "flatten", return_value=[]):
            with self.assertRaises(ValueError):
                dispatcher.snapshot(12)

    def test_dependency_api_failure_does_not_mean_no_blockers(self):
        with patch.object(dispatcher, "api", return_value=self.issue), patch.object(dispatcher, "flatten", side_effect=RuntimeError("offline")):
            with self.assertRaises(RuntimeError):
                dispatcher.snapshot(12)

    def test_closed_blocker_requires_merged_closing_pr_in_main(self):
        self.issue["blockers"] = [3]
        blocker = {"state": "closed", "state_reason": "completed", "html_url": "https://github.com/example/issues/3"}
        pr = {"baseRefName": "main", "mergeCommit": {"oid": "abc"}, "closingIssuesReferences": [{"url": blocker["html_url"]}]}
        with patch.object(dispatcher, "api", return_value=blocker), patch.object(dispatcher.subprocess, "run") as run:
            self.assertFalse(dispatcher.blockers_complete(self.issue, [], Path(".")))
            run.return_value.returncode = 1
            self.assertFalse(dispatcher.blockers_complete(self.issue, [pr], Path(".")))
            run.return_value.returncode = 0
            self.assertTrue(dispatcher.blockers_complete(self.issue, [pr], Path(".")))
            blocker["state_reason"] = "not_planned"
            self.assertFalse(dispatcher.blockers_complete(self.issue, [pr], Path(".")))

    def test_second_process_cannot_acquire_active_lock(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "lock"
            with dispatcher.lock(path):
                result = subprocess.run(["python3", "-c",
                    "import fcntl,sys; f=open(sys.argv[1],'a+'); fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)",
                    str(path)], capture_output=True)
                self.assertNotEqual(result.returncode, 0)
            with dispatcher.lock(path):
                pass

    def test_worker_failure_preserves_attempt_and_never_pushes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "docs/agents").mkdir(parents=True)
            (root / "docs/agents/worker.md").write_text("Trusted worker instructions")
            records = {}
            with patch.object(dispatcher, "command", return_value="base") as cmd, \
                 patch.object(dispatcher, "edit") as edit, \
                 patch.object(dispatcher, "run_process", side_effect=RuntimeError("worker failed")):
                with self.assertRaisesRegex(RuntimeError, "worker failed"):
                    dispatcher.implement(self.issue, root, root, records, 1)
                self.assertEqual(json.loads((root / "runs.json").read_text())["12"]["status"], "blocked")
                self.assertTrue((root / "runs/12/ticket.json").exists())
                self.assertFalse(any("push" in call.args[0] for call in cmd.call_args_list))
                self.assertEqual(edit.call_args.args[1], "agent:blocked")


class PipelineTests(unittest.TestCase):
    """Real Git/worktrees/processes, with disposable Codex and GitHub executables."""

    def run_pipeline(self, verification_exit):
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            root, remote, binaries = temp / "repo", temp / "remote.git", temp / "bin"
            root.mkdir()
            binaries.mkdir()
            def git(*args):
                return dispatcher.command(["git", *args], root)
            git("init", "-b", "main")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "commit.gpgsign", "false")
            (root / "docs/agents").mkdir(parents=True)
            (root / "docs/agents/worker.md").write_text("Implement the fixture")
            (root / "scripts").mkdir()
            check = root / "scripts/build-and-test.sh"
            check.write_text(f"#!/bin/sh\nexit {verification_exit}\n")
            check.chmod(0o755)
            (root / "value.txt").write_text("before\n")
            git("add", ".")
            git("commit", "-m", "chore: fixture")
            dispatcher.command(["git", "init", "--bare", str(remote)])
            git("remote", "add", "origin", str(remote))
            git("push", "origin", "main")
            codex = binaries / "codex"
            codex.write_text("#!/usr/bin/env python3\nimport json, pathlib, subprocess, sys\n"
                "sys.stdin.read()\npathlib.Path('value.txt').write_text('after\\n')\n"
                "subprocess.run(['git','commit','-am','fix: update fixture'],check=True)\n"
                "pathlib.Path(sys.argv[sys.argv.index('-o')+1]).write_text(json.dumps("
                "{'status':'ready','summary':'Updated fixture','verification':'fixture check passed'}))\n")
            codex.chmod(0o755)
            gh = binaries / "gh"
            gh.write_text("#!/usr/bin/env python3\nimport os, pathlib, sys\n"
                "p=pathlib.Path(os.environ['FIXTURE_GH_LOG'])\n"
                "with p.open('a') as f: f.write(repr(sys.argv[1:])+'\\n')\n"
                "if sys.argv[1:3]==['pr','create']: print('https://github.com/example/repo/pull/1')\n")
            gh.chmod(0o755)
            state = root / ".git/agent-dispatch"
            state.mkdir()
            issue = ticket()
            current = copy.deepcopy(issue)
            current["labels"] = [{"name": "agent:running"}]
            records = {}
            with patch.dict(os.environ, {"PATH": str(binaries) + os.pathsep + os.environ["PATH"],
                                        "FIXTURE_GH_LOG": str(temp / "gh.log")}), \
                 patch.object(dispatcher, "snapshot", return_value=current), dispatcher.lock(state / "lock") as fd:
                if verification_exit:
                    with self.assertRaisesRegex(RuntimeError, "Command failed"):
                        dispatcher.implement(issue, root, state, records, fd)
                else:
                    dispatcher.implement(issue, root, state, records, fd)
            log = (temp / "gh.log").read_text()
            if verification_exit:
                self.assertNotIn("'pr', 'create'", log)
                self.assertEqual(records["12"]["status"], "blocked")
                remote_result = subprocess.run(["git", "--git-dir", str(remote), "rev-parse", "--verify",
                    "refs/heads/codex/issue-12"], capture_output=True)
                self.assertNotEqual(remote_result.returncode, 0)
            else:
                self.assertIn("'--draft'", log)
                self.assertEqual(records["12"]["status"], "review")
                self.assertEqual(dispatcher.command(["git", "--git-dir", str(remote), "show",
                    "refs/heads/codex/issue-12:value.txt"]), "after")
            self.assertEqual(git("branch", "--show-current"), "main")
            self.assertEqual((root / "value.txt").read_text(), "before\n")

    def test_verified_worker_publishes_draft_from_isolated_worktree(self):
        self.run_pipeline(0)

    def test_failed_verification_keeps_work_local(self):
        self.run_pipeline(1)


if __name__ == "__main__":
    unittest.main()
