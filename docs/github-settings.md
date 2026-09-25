# GitHub repository settings

These settings are configured on GitHub, separately from the files in this repo.
Apply them after the PR introducing `pr-title` has run successfully and merged.
GitHub documents the steps for [creating rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository).

## Merge settings

Under **Settings → General → Pull Requests**:

- Enable **Allow squash merging** and choose **Pull request title** as the default
  commit message title.
- Disable **Allow merge commits** and **Allow rebase merging**.
- Enable **Automatically delete head branches**.

Before confirming a squash merge, preserve the validated Conventional Commit PR
title as the commit title. A PR title check cannot prevent a maintainer from
editing the squash commit message in the merge dialog.

## Main branch ruleset

Under **Settings → Rules → Rulesets**, create a branch ruleset named `main`:

| Setting | Value |
| --- | --- |
| Enforcement | Active |
| Target branch | `main` only |
| Bypass list | Empty |
| Restrict deletions | Enabled |
| Block force pushes | Enabled |
| Require linear history | Enabled |
| Require a pull request before merging | Enabled |
| Require conversation resolution before merging | Enabled |
| Require status checks to pass | `verify`, `analyze-csharp`, `pr-title` |
| Require branches to be up to date before merging | Enabled |

Select the checks reported by GitHub Actions, with GitHub Actions as their expected
source. If `pr-title` is absent, let its first PR run finish before adding it.
Leave **Restrict updates** off; PRs that satisfy the rules must be able to merge.
Do not add Dependabot or the release preparation App to the bypass list.

For a sole maintainer, set required approvals to zero. PRs and passing checks
remain required, and the maintainer reviews bot and agent changes before merging.
When a second maintainer is available, require one approval and enable **Dismiss
stale pull request approvals when new commits are pushed**. Authors cannot approve
their own PRs, so requiring an approval without another reviewer blocks their work.

Scope these rules to `main`, not every branch. Dependabot must be able to rebase
its PR branches, and release preparation automation must be able to regenerate
its preparation branch. Keep Dependabot's default automatic rebase strategy.

## Release tags

Create a separate tag ruleset named `release-tags`, targeting `v*`, with enforcement
**Active**, an empty bypass list, **Restrict updates**, and **Restrict deletions**.
This protects existing release tags while allowing new tags to be created.

Do not enable **Restrict creations** with an empty bypass list. The current Release
workflow creates tags using `GITHUB_TOKEN`; that would block manual workflow
releases. To restrict tag creation to particular actors later, first configure
and verify a dedicated release App token for the publishing workflow. Then add a
separate creation-only tag ruleset with the release App and authorized maintainer
role as bypass actors. Keep the no-update/no-delete ruleset without bypasses.
The App used to open preparation PRs does not automatically give the publishing
workflow an App identity.

## Changelog and existing PRs

Dependency PRs update dependencies. Collect their release notes in the release
preparation PR instead of adding a changelog edit to each Dependabot branch.
When using the automated preparation workflow, dependency entries require an
explicit version bump, such as `patch`.

Prepare release notes after merging the intended changes. If more changes merge
while preparation is open, regenerate and review the notes before merging the
release PR. Updating its base or resolving conflicts alone does not refresh
generated notes. Publish before merging further product or dependency changes.

The Dependabot title configuration applies to new PRs. Check existing PR titles
and rename any that do not follow Conventional Commit syntax. In particular,
PR #5 currently needs a `chore(deps):` prefix. Existing `build(deps):` titles are
valid too.

The automated release preparation work is separate from this configuration.
Before its first release, review commits since the previous release tag for
older non-Conventional titles and merge commits. These guardrails only govern
future merges; they do not repair existing history. Do not rewrite published
history or move release tags to satisfy the parser.
