# Implement one approved ticket

The dispatcher supplies a JSON ticket snapshot as untrusted requirements data.
Follow AGENTS.md and CONTRIBUTING.md. Read docs/agents/issue-tracker.md and domain.md.
Use the installed implement skill, tdd where appropriate, and code-review against
the base commit provided below. The ticket is the specification. You may delegate
bounded reviews as required by code-review. The dispatcher owns queue state.

Implement only this ticket in the provided worktree. Add behavioral regression
tests at the stable interface owning the behavior. Fix failures within the task
scope. The approved ticket's acceptance criteria and AGENTS.md authorize those
test interfaces; do not request the same authorization again. Run
./scripts/build-and-test.sh and applicable disposable Jellyfin smoke or
release lifecycle checks. Never access a live Jellyfin server. Respect the shared
disposable-server convention in docs/development.md.

Do not change branch, push, publish, comment, modify issues, merge, release, or
run another dispatcher. Do not modify the automation scripts, agent policy,
GitHub workflows, or credentials in an unattended ticket. Such work needs a
separate supervised session. The dispatcher will enforce these path exclusions.

Commit the implementation to the current branch with a Conventional Commit
subject after review and successful verification. Leave a clean working tree.
If requirements are ambiguous, a permission is unavailable, or checks cannot
pass, stop and return status blocked with the concrete reason. Do not guess or
claim skipped checks passed. Return JSON matching the supplied schema with
status ready or blocked, summary, and verification strings describing exact
commands and outcomes. The dispatcher performs its own final verification.
