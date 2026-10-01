"""Queue and execution behavior at the dispatcher boundary, without live GitHub."""

import copy
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
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

    def test_blocker_lists_preserve_every_complete_reference(self):
        for blockers, expected in [("None", []), ("NONE", []), ("#3", [3]), ("#3, #4", [3, 4]),
                ("#3 #4", [3, 4]), ("- #3\n- #4", [3, 4]), ("* #3\n\n* #4", [3, 4]),
                ("#3\n#4", [3, 4]), ("#3, #3, #4", [3, 4])]:
            with self.subTest(blockers=blockers):
                issue = copy.deepcopy(self.issue)
                issue["body"] = issue["body"].replace("\nNone\n", f"\n{blockers}\n")
                with patch.object(dispatcher, "api", return_value=issue), \
                     patch.object(dispatcher, "flatten", return_value=[]):
                    self.assertEqual(dispatcher.snapshot(12)["blockers"], expected)

    def test_partial_blocker_references_are_rejected_instead_of_dropped(self):
        for blockers in ("3, #4", "#3, 4", "- 3\n- #4", "#3-#4", "#3#4", "#3,", "#0, #4",
                         "#3, None", "#3 trailing", "#3, ##4", "#3, #４", "3"):
            with self.subTest(blockers=blockers):
                issue = copy.deepcopy(self.issue)
                issue["body"] = issue["body"].replace("\nNone\n", f"\n{blockers}\n")
                with patch.object(dispatcher, "api", return_value=issue), \
                     patch.object(dispatcher, "flatten", return_value=[]):
                    with self.assertRaisesRegex(ValueError, "#NUMBER"):
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

    def test_dry_run_explains_why_approved_queue_cannot_start(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            state = root / "agent-dispatch"
            state.mkdir()
            issues = [copy.deepcopy(self.issue) for _ in range(3)]
            for number, issue in zip((12, 13, 14), issues):
                issue["number"] = number
            issues[0]["blockers"] = [20]
            issues[1]["labels"].append({"name": "ready-for-human"})
            dispatcher.save(state / "approvals.json", {
                str(issue["number"]): dispatcher.fingerprint(issue) for issue in issues[:2]})
            output = io.StringIO()
            with patch.object(sys, "argv", ["agent-dispatch.py", "dry-run"]), \
                 patch.object(dispatcher, "command", return_value=str(root)), \
                 patch.object(dispatcher, "pull_requests", return_value=[]), \
                 patch.object(dispatcher, "flatten", return_value=issues), \
                 patch.object(dispatcher, "snapshot", side_effect=lambda n: next(i for i in issues if i["number"] == n)), \
                 patch.object(dispatcher, "blockers_complete", return_value=False), \
                 patch.object(dispatcher, "implement") as implement, contextlib.redirect_stdout(output):
                dispatcher.main()
            result = json.loads(output.getvalue())
            self.assertEqual(result["status"], "idle")
            self.assertEqual(result["tickets"], [])
            self.assertTrue(any("#12" in reason and "#20" in reason for reason in result["skipped"]))
            self.assertTrue(any("#13" in reason and "ready-for-human" in reason for reason in result["skipped"]))
            self.assertTrue(any("#14" in reason and "approval" in reason for reason in result["skipped"]))
            implement.assert_not_called()


class PipelineTests(unittest.TestCase):
    """Real Git/worktrees/processes, with disposable Codex and GitHub executables."""

    def run_pipeline(self, verification_exit, backlog=False, change="ordinary", existing_pr=None,
                     protected_path=None, current_changes=None):
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
            (root / "AGENTS.md").write_text("Supervised policy\n")
            (root / "scripts").mkdir()
            check = root / "scripts/build-and-test.sh"
            check.write_text(f"#!/bin/sh\nexit {verification_exit}\n")
            check.chmod(0o755)
            if protected_path:
                protected = root / protected_path
                protected.parent.mkdir(parents=True, exist_ok=True)
                if not protected.exists():
                    protected.write_text("Supervised policy or automation\n")
            (root / "value.txt").write_text("before\n")
            git("add", ".")
            git("commit", "-m", "chore: fixture")
            dispatcher.command(["git", "init", "--bare", str(remote)])
            git("remote", "add", "origin", str(remote))
            git("push", "origin", "main")
            worker_changes = {
                "ordinary": "pathlib.Path('value.txt').write_text('after\\n')\n",
                "policy-rename": "pathlib.Path('AGENTS.md').rename('docs/policy.md')\n",
                "quoted-path": "p=pathlib.Path('.github/workflows/café.yml'); p.parent.mkdir(parents=True); p.write_text('changed\\n')\n",
                "newline-path": "p=pathlib.Path('.github/workflows/new\\nworkflow.yml'); p.parent.mkdir(parents=True); p.write_text('changed\\n')\n",
                "ordinary-rename": "pathlib.Path('value.txt').rename('renamed.txt')\n",
                "script-test": "pathlib.Path('value.txt').write_text('after\\n')\np=pathlib.Path('scripts/tests/test_fixture.py'); p.parent.mkdir(parents=True); p.write_text('assert True\\n')\n",
            }
            worker_change = (f"pathlib.Path({protected_path!r}).write_text('#!/bin/sh\\nexit 0\\n')\n"
                             if protected_path else worker_changes[change])
            codex = binaries / "codex"
            codex.write_text("#!/usr/bin/env python3\nimport json, pathlib, subprocess, sys\n"
                "if sys.argv[1]=='sandbox':\n"
                " sys.exit(subprocess.run(sys.argv[sys.argv.index('--')+1:]).returncode)\n"
                "if '--approve-for-me' not in sys.argv or '--sandbox' in sys.argv:\n"
                " sys.exit('Use automatic approval review without the conflicting sandbox flag')\n"
                "sys.stdin.read()\n" + worker_change +
                "subprocess.run(['git','add','-A'],check=True)\n"
                "subprocess.run(['git','commit','-m','fix: update fixture'],check=True)\n"
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
            current.update(current_changes or {})
            records = {}
            blocked = verification_exit or backlog or existing_pr or protected_path or current_changes or change in {
                "policy-rename", "quoted-path", "newline-path"}
            with patch.dict(os.environ, {"PATH": str(binaries) + os.pathsep + os.environ["PATH"],
                                        "FIXTURE_GH_LOG": str(temp / "gh.log")}), \
                 patch.object(dispatcher, "snapshot", return_value=current), dispatcher.lock(state / "lock") as fd:
                prs = ([{"headRefName": f"codex/issue-{n}", "closingIssuesReferences": []}
                        for n in (1, 2)] if backlog else [existing_pr] if existing_pr else [])
                with patch.object(dispatcher, "pull_requests", return_value=prs):
                    if blocked:
                        with self.assertRaisesRegex(RuntimeError, "Command failed|Review backlog filled|protected automation/policy|Another implementation PR|Ticket approval, assignment or scope changed"):
                            dispatcher.implement(issue, root, state, records, fd)
                    else:
                        dispatcher.implement(issue, root, state, records, fd)
            log = (temp / "gh.log").read_text()
            if blocked:
                self.assertNotIn("'pr', 'create'", log)
                self.assertEqual(records["12"]["status"], "blocked")
                remote_result = subprocess.run(["git", "--git-dir", str(remote), "rev-parse", "--verify",
                    "refs/heads/codex/issue-12"], capture_output=True)
                self.assertNotEqual(remote_result.returncode, 0)
                self.assertEqual(dispatcher.command(["git", "-C", records["12"]["worktree"],
                    "log", "-1", "--format=%s"]), "fix: update fixture")
                if protected_path:
                    self.assertFalse((state / "runs/12/verify.log").exists())
            else:
                self.assertIn("'--draft'", log)
                self.assertEqual(records["12"]["status"], "review")
                path = "renamed.txt" if change == "ordinary-rename" else "value.txt"
                self.assertEqual(dispatcher.command(["git", "--git-dir", str(remote), "show",
                    f"refs/heads/codex/issue-12:{path}"]), "before" if change == "ordinary-rename" else "after")
            self.assertEqual(git("branch", "--show-current"), "main")
            self.assertEqual((root / "value.txt").read_text(), "before\n")

    def test_verified_worker_publishes_draft_from_isolated_worktree(self):
        self.run_pipeline(0)

    def test_failed_verification_keeps_work_local(self):
        self.run_pipeline(1)

    def test_backlog_filling_during_implementation_keeps_work_local(self):
        self.run_pipeline(0, backlog=True)

    def test_protected_rename_source_keeps_work_local(self):
        self.run_pipeline(0, change="policy-rename")

    def test_git_quoted_protected_path_keeps_work_local(self):
        self.run_pipeline(0, change="quoted-path")

    def test_protected_path_with_newline_keeps_work_local(self):
        self.run_pipeline(0, change="newline-path")

    def test_ordinary_rename_can_publish(self):
        self.run_pipeline(0, change="ordinary-rename")

    def test_closing_pr_appearing_during_implementation_keeps_work_local(self):
        self.run_pipeline(0, existing_pr={"headRefName": "manual-fix",
            "closingIssuesReferences": [{"url": ticket()["html_url"]}]})

    def test_branch_pr_appearing_during_implementation_keeps_work_local(self):
        self.run_pipeline(0, existing_pr={"headRefName": "codex/issue-12", "closingIssuesReferences": []})

    def test_substantive_policy_documents_require_supervision(self):
        for path in ("docs/contributing.md", "docs/development.md", "docs/releasing.md",
                     "docs/release-builds.md", "docs/github-settings.md"):
            with self.subTest(path=path):
                self.run_pipeline(0, protected_path=path)

    def test_verification_entrypoint_cannot_be_replaced_to_hide_failure(self):
        self.run_pipeline(1, protected_path="scripts/build-and-test.sh")

    def test_other_automation_scripts_require_supervision(self):
        for path in ("scripts/check-public-tree.sh", "scripts/build.sh", "scripts/dev.py"):
            with self.subTest(path=path):
                self.run_pipeline(0, protected_path=path)

    def test_scoped_agent_policy_requires_supervision_even_inside_tests(self):
        self.run_pipeline(0, protected_path="scripts/tests/AGENTS.override.md")

    def test_script_behavior_tests_remain_available_to_workers(self):
        self.run_pipeline(0, change="script-test")

    def test_assignment_during_implementation_keeps_work_local(self):
        self.run_pipeline(0, current_changes={"assignees": [{"login": "maintainer"}]})

    def test_changed_scope_or_queue_state_during_implementation_keeps_work_local(self):
        for changes in ({"body": ticket()["body"] + "\nChanged scope"}, {"state": "closed"},
                        {"labels": []}, {"labels": [{"name": "agent:running"}, {"name": "needs-triage"}]}):
            with self.subTest(changes=changes):
                self.run_pipeline(0, current_changes=changes)


if __name__ == "__main__":
    unittest.main()
