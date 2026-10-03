"""Fail-closed review and CI gates without touching live GitHub."""
import copy
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("agent_merge", Path(__file__).parents[1] / "agent-merge.py")
merge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(merge)


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.issue = {"number": 23, "title": "Direct Apply", "body": "Approved scope", "blockers": [],
                      "state": "open", "labels": [{"name": "agent:review"}], "assignees": []}
        self.approval = merge.dispatcher.fingerprint(self.issue)
        self.pr = {"number": 31, "state": "open", "draft": False, "user": {"login": "sntna"},
                   "base": {"sha": "base", "ref": "main", "repo": {"full_name": merge.REPO}},
                   "head": {"sha": "head", "ref": "codex/issue-23", "repo": {"full_name": merge.REPO}},
                   "mergeable": True, "mergeable_state": "clean"}
        self.review = {"repository": merge.REPO, "pr": 31, "base": "base", "head": "head",
                       "approval": self.approval, "status": "passed",
                       "standards": {"status": "passed", "findings": [], "reviewer": "a", "report": "Reviewed diff"},
                       "spec": {"status": "passed", "findings": [], "reviewer": "b", "report": "Matches scope"},
                       "verification": {"head": "head", "status": "passed", "report": "Build and applicable smoke passed"}}
        self.checks = [{"name": name, "bucket": "pass"} for name in merge.REQUIRED_CHECKS]

    def test_matching_independent_review_and_checks_pass(self):
        merge.validate_review(self.review, self.pr, self.approval)
        merge.validate_pr(self.pr, self.issue, self.approval)
        merge.validate_checks(self.checks, self.checks)

    def test_changed_revision_or_scope_cannot_reuse_approval(self):
        for field in ("repository", "pr", "base", "head", "approval", "status"):
            with self.subTest(field=field):
                review = dict(self.review, **{field: "changed"})
                with self.assertRaises(ValueError):
                    merge.validate_review(review, self.pr, self.approval)
        changed = dict(self.issue, body="New scope")
        with self.assertRaises(ValueError):
            merge.validate_pr(self.pr, changed, self.approval)

    def test_missing_review_findings_or_same_reviewer_block_merge(self):
        for axis in ("standards", "spec"):
            for field, value in (("status", "failed"), ("findings", ["Fix data loss"]),
                                 ("reviewer", ""), ("report", "")):
                review = copy.deepcopy(self.review)
                review[axis][field] = value
                with self.assertRaises(ValueError):
                    merge.validate_review(review, self.pr, self.approval)
        self.review["spec"]["reviewer"] = "a"
        with self.assertRaises(ValueError):
            merge.validate_review(self.review, self.pr, self.approval)

    def test_unverified_head_or_missing_evidence_blocks_merge(self):
        for field, value in (("head", "old"), ("status", "failed"), ("report", "")):
            review = copy.deepcopy(self.review)
            review["verification"][field] = value
            with self.assertRaises(ValueError):
                merge.validate_review(review, self.pr, self.approval)

    def test_missing_pending_failed_and_skipped_required_checks_block_merge(self):
        with self.assertRaises(ValueError):
            merge.validate_checks([], [])
        for bucket in ("pending", "fail", "cancel", "skipping"):
            checks = copy.deepcopy(self.checks)
            checks[0]["bucket"] = bucket
            with self.assertRaises(ValueError):
                merge.validate_checks(checks, checks)
        with self.assertRaises(ValueError):
            merge.validate_checks(self.checks + [{"name": "extra", "bucket": "fail"}], self.checks)

    def test_external_closed_unmergeable_and_wrong_branch_prs_are_rejected(self):
        for field, value in (("state", "closed"), ("mergeable", None), ("mergeable_state", "behind"),
                             ("user", {"login": "outsider"})):
            with self.assertRaises(ValueError):
                merge.validate_pr(dict(self.pr, **{field: value}), self.issue, self.approval)
        for field in ("head", "base"):
            pr = copy.deepcopy(self.pr)
            pr[field]["repo"]["full_name"] = "other/repo"
            with self.assertRaises(ValueError):
                merge.validate_pr(pr, self.issue, self.approval)
        self.pr["head"]["ref"] = "unapproved"
        with self.assertRaises(ValueError):
            merge.validate_pr(self.pr, self.issue, self.approval)


class MergeCommandTests(unittest.TestCase):
    def setUp(self):
        MergeTests.setUp(self)

    def run_command(self, mutate=None, pending=False, files=None, allowed_paths=(), current_paths=None):
        import contextlib
        import io
        import json
        import tempfile
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temp:
            common = Path(temp)
            (common / 'agent-dispatch').mkdir()
            (common / 'agent-dispatch/approvals.json').write_text(json.dumps({'23': self.approval}))
            (common / 'agent-dispatch/runs.json').write_text(json.dumps({'23': {
                'branch': 'codex/issue-23', 'allowed_protected_paths': list(allowed_paths)}}))
            (common / 'agent-dispatch/protected-paths.json').write_text(json.dumps({'23': {
                'fingerprint': self.approval, 'paths': list(allowed_paths if current_paths is None else current_paths)}}))
            review_file = common / 'review.json'
            review_file.write_text(json.dumps(self.review))
            pr = copy.deepcopy(self.pr)
            pr['title'] = 'feat: direct Apply'
            files = files if files is not None else [{'filename': 'plugin.cs'}]
            pr['changed_files'] = len(files)
            if mutate:
                mutate(pr)
            calls = []
            merged = False

            def api(endpoint):
                if endpoint == 'user':
                    return {'login': 'sntna'}
                if endpoint.endswith('/commits/main'):
                    return {'sha': 'base'}
                return dict(pr, merged=merged, merge_commit_sha='merge-sha')

            def command(args):
                nonlocal merged
                calls.append(args)
                if args[:2] == ['git', 'rev-parse']:
                    return str(common)
                if args[:3] == ['gh', 'pr', 'checks']:
                    if pending:
                        raise RuntimeError('Checks are pending')
                    return json.dumps(self.checks)
                if args[:3] == ['gh', 'pr', 'merge']:
                    merged = True
                    return ''
                raise AssertionError(args)

            with patch.object(merge.sys, 'argv', ['agent-merge.py', 'merge', '31', '--review', str(review_file)]), \
                 patch.object(merge.dispatcher, 'api', side_effect=api), \
                 patch.object(merge.dispatcher, 'snapshot', return_value=self.issue), \
                 patch.object(merge.dispatcher, 'flatten', return_value=files), \
                 patch.object(merge.dispatcher, 'command', side_effect=command), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                error = None
                try:
                    merge.main()
                except (ValueError, RuntimeError) as exc:
                    error = str(exc)
            return calls, output.getvalue(), error

    def test_merge_command_pins_reviewed_head_and_confirms_result(self):
        calls, output, error = self.run_command()
        self.assertIsNone(error)
        commands = [args for args in calls if args[:3] == ['gh', 'pr', 'merge']]
        self.assertEqual(commands, [['gh', 'pr', 'merge', '31', '--repo', merge.REPO, '--squash',
                                     '--match-head-commit', 'head', '--subject', 'feat: direct Apply']])
        self.assertIn('"status": "merged"', output)

    def test_changed_head_or_base_cannot_reach_merge(self):
        for side in ('base', 'head'):
            calls, _, error = self.run_command(lambda pr: pr[side].update(sha='changed'))
            self.assertIsNotNone(error)
            self.assertFalse(any(args[:3] == ['gh', 'pr', 'merge'] for args in calls))

    def test_pending_checks_or_draft_cannot_reach_merge(self):
        for kwargs in ({'pending': True}, {'mutate': lambda pr: pr.update(draft=True)}):
            calls, _, error = self.run_command(**kwargs)
            self.assertIsNotNone(error)
            self.assertFalse(any(args[:3] == ['gh', 'pr', 'merge'] for args in calls))

    def test_repaired_protected_paths_and_revoked_permissions_cannot_reach_merge(self):
        for kwargs in (
                {'files': [{'filename': 'scripts/build-and-test.sh'}]},
                {'files': [{'filename': 'new-policy.md', 'previous_filename': 'AGENTS.md'}]},
                {'files': [{'filename': 'scripts/dashboard-smoke.py'}],
                 'allowed_paths': ('scripts/dashboard-smoke.py',), 'current_paths': ()}):
            calls, _, error = self.run_command(**kwargs)
            self.assertIsNotNone(error)
            self.assertFalse(any(args[:3] == ['gh', 'pr', 'merge'] for args in calls))

    def test_explicitly_authorized_smoke_repair_can_merge(self):
        calls, _, error = self.run_command(files=[{'filename': 'scripts/dashboard-smoke.py'}],
                                           allowed_paths=('scripts/dashboard-smoke.py',))
        self.assertIsNone(error)
        self.assertTrue(any(args[:3] == ['gh', 'pr', 'merge'] for args in calls))

    def test_repair_publication_checks_committed_diff_against_permission_snapshot(self):
        import tempfile
        from unittest.mock import patch
        for changed_path in ('plugin.cs', 'scripts/agent-dispatch.py'):
            with tempfile.TemporaryDirectory() as temp:
                state = Path(temp)
                def command(args, cwd=None):
                    if args[1] == 'branch':
                        return 'codex/issue-23'
                    if args[1] == 'diff':
                        return changed_path + chr(0)
                    if args[1] == 'rev-parse':
                        return 'new-head'
                    return ''
                with patch.object(merge, 'approved_ticket', return_value=(self.pr, self.issue, self.approval,
                                   {'allowed_protected_paths': []})), \
                     patch.object(merge.dispatcher, 'command', side_effect=command):
                    if changed_path == 'plugin.cs':
                        self.assertEqual(merge.check_repair_paths(31, state, state), 'new-head')
                    else:
                        with self.assertRaises(RuntimeError):
                            merge.check_repair_paths(31, state, state)
