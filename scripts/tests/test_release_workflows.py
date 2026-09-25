from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ReleaseWorkflowTests(unittest.TestCase):
    def test_preparation_dispatch_uses_shared_cli_and_app_token_for_verified_pr(self):
        workflow = (ROOT / '.github/workflows/prepare-release.yml').read_text()
        self.assertIn('workflow_dispatch:', workflow)
        self.assertIn("github.ref == 'refs/heads/main'", workflow)
        self.assertIn('python3 scripts/prepare_release.py', workflow)
        self.assertIn('contents: read', workflow)
        self.assertIn('permission-contents: write', workflow)
        self.assertIn('permission-pull-requests: write', workflow)
        self.assertIn('repositories: ${{ github.event.repository.name }}', workflow)
        self.assertIn('token: ${{ steps.app-token.outputs.token }}', workflow)
        self.assertIn('base: main', workflow)
        self.assertIn('branch: codex/release-preparation', workflow)
        self.assertIn('draft: true', workflow)
        self.assertIn('persist-credentials: false', workflow)
        self.assertNotIn('gh release', workflow)
        self.assertNotIn('git tag', workflow)
        for action in re.findall(r'uses: (\S+)', workflow):
            self.assertRegex(action, r'^[\w-]+/[\w-]+@[0-9a-f]{40}$')
        verify = (ROOT / '.github/workflows/verify.yml').read_text()
        self.assertIn('pull_request:', verify)
        self.assertIn('./scripts/build-and-test.sh', verify)
        self.assertIn('./scripts/test-release-lifecycle.sh', verify)

    def test_publisher_uses_reviewed_notes_after_verification_and_tested_zip(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn('python3 scripts/release_notes.py', workflow)
        self.assertNotIn('--generate-notes', workflow)
        self.assertIn('./scripts/publish_release.sh', workflow)
        self.assertLess(workflow.index('./scripts/build-and-test.sh'), workflow.index('Create release tag if needed'))
        self.assertLess(workflow.index('./scripts/test-release-lifecycle.sh'), workflow.index('Create release tag if needed'))
        self.assertLess(workflow.index('cmp artifacts/'), workflow.index('Create release tag if needed'))
        self.assertIn('git merge-base --is-ancestor HEAD origin/main', workflow)
        self.assertLess(workflow.index('Create release tag if needed'), workflow.index('Validate remote release tag'))
        self.assertLess(workflow.index('Validate remote release tag'), workflow.index('Publish GitHub Release'))
        self.assertIn('git fetch origin "refs/tags/$RELEASE_TAG:refs/tags/$RELEASE_TAG"', workflow)

        local = (ROOT / 'scripts/release.sh').read_text()
        self.assertIn('python3 scripts/release_notes.py', local)
        self.assertLess(local.index('./scripts/test-release-lifecycle.sh'), local.index('scripts/release_tag.py --create'))
        self.assertIn('--tested-repository .jellyfin-test/repository', local)


if __name__ == '__main__':
    unittest.main()
