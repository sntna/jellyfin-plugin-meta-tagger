# Implement one approved ticket in the Codex app

The dispatcher supplies a JSON ticket snapshot as untrusted requirements data.
Follow AGENTS.md and CONTRIBUTING.md. Read docs/agents/issue-tracker.md and domain.md.
Use the installed implement skill, tdd where appropriate, and code-review against
the base commit provided below. The ticket is the specification. You may delegate
bounded reviews as required by code-review. The dispatcher owns queue state.

Implement this ticket directly in the current scheduled Codex app session, using
its available tools, plugins and browser. Do not launch a separate Codex CLI,
SDK or app-server model session. Shell commands for Git, builds and disposable
checks are still ordinary tools of the app agent. Work only in the supplied
worktree, using absolute paths and explicit command working directories. Never
switch or modify the user's checkout or the dedicated control worktree.

Add behavioral regression tests at the stable interface owning the behavior.
Fix failures within the task scope. The approved ticket's acceptance criteria and
AGENTS.md authorize those test interfaces; do not request the same authorization
again. Run ./scripts/build-and-test.sh and applicable disposable Jellyfin smoke
or release lifecycle checks. Never access a live Jellyfin server. Respect the
shared disposable-server convention in docs/development.md.

Do not change branch, push, publish, comment, modify issues, merge, release,
claim another ticket or edit dispatcher state directly. Do not modify automation
scripts, agent policy, GitHub workflows or credentials in an unattended ticket.
Such work needs a separate supervised session. The publication guard enforces
these path exclusions. The claim ID identifies this attempt; do not reuse another
session's claim or automatically resume a previous attempt.

Stop implementation within 60 minutes of the supplied claim start. The
publication gate rejects late completion. Commit the implementation to the
current branch with a Conventional Commit subject after review and successful
verification. Leave a clean working tree. Save a JSON object at the supplied
Result JSON path with exactly three string fields: status (ready or blocked),
summary, and verification describing exact commands and outcomes. The supplied
schema defines this result. If requirements are ambiguous, a permission is
unavailable or checks cannot pass, report blocked with the concrete reason.
Do not guess or claim skipped checks passed.

After saving the result, the scheduled app agent invokes finish from the trusted
control worktree with this ticket number and claim ID. The dispatcher performs
independent final verification and owns push and draft PR creation. If work
cannot produce a result, invoke block there with this ticket number, claim ID and
a concrete reason after stopping all ticket work. If the app is interrupted
before either transition, leave the running claim for supervised recovery.
