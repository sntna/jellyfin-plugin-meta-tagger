# Local ticket automation

Tickets live as GitHub Issues in `sntna/jellyfin-plugin-meta-tagger`. The local
dispatcher checks the queue through a Codex scheduled task. The task's configured
schedule controls the cadence; inspect it in Codex when diagnosing missed pickup.
Keep the Mac on, Codex running, and OrbStack available for integration checks.
The schedule starts dispatching only after this setup is merged into main.

## Prepare tickets

Use `/grill-with-docs`, `/to-spec`, then `/to-tickets` for planned features.
The last skill publishes each approved implementation ticket as a GitHub Issue
and invokes the approval command below. Incoming reports go through `/triage`
before a maintainer approves their scope. `/implement` works one ticket in a
fresh session. A spec or parent issue is not an implementation ticket.

The required ticket headings are Objective or What to build, In scope, Out of
scope, Acceptance criteria, Blocked by, and Priority. Include expected tests in
Acceptance criteria. The agent-task issue form provides these fields.

```sh
python3 scripts/agent-dispatch.py labels
python3 scripts/agent-dispatch.py approve 123
python3 scripts/agent-dispatch.py dry-run
python3 scripts/agent-dispatch.py run
python3 scripts/agent-dispatch.py status
```

`labels` creates or updates queue labels. `approve` requires an authenticated
GitHub user with repository write access. It records the ticket title, body and
dependencies and applies `agent:ready`. Scope edits need renewed approval.
Approval by the user of a `/to-tickets` breakdown authorizes publishing and
approving those tickets; do not ask them to approve the same scope again.

`dry-run` fetches main and reads GitHub without claiming tickets or invoking a
worker. It may update local Git refs and initialize local state. `run` picks one
ticket, by High/Normal/Low priority then oldest first. Only approved, unassigned,
unblocked tickets qualify. An existing closing PR or saved attempt prevents
duplicate work. Dependencies must have a merged closing PR included in main.
The native dependency endpoint must work; API errors stop dispatch rather than
silently treating dependencies as empty.
An `idle` result means no eligible ticket, even when issues carry `agent:ready`.
Read `skipped` for missing local approvals, supervised labels, changed scope,
saved attempts, existing PRs and incomplete dependencies. Use `status` to inspect
saved blocked attempts whose labels no longer put them in the ready queue.
Text blockers must be complete positive `#NUMBER` references, separated by commas,
spaces or lines, optionally with `-` or `*` list bullets. Malformed references are
rejected rather than omitted. Immediately before publication the ticket must
still have its approved scope, be open and unassigned, and carry `agent:running`
without an excluded queue label.

## Worker environment

Install `gh`, Git, Python 3.11+, Node.js 20+, the SDK in `global.json`, Docker or
OrbStack, and Codex CLI. Authenticate using `gh auth login` and `codex login`.
Install implement, tdd, and code-review under `~/.agents/skills/`, including their
referenced resources. The worker uses existing Codex authentication and model
configuration. It runs with workspace-write sandboxing and automatic approval
review; unavailable permissions produce a blocked attempt rather than disabling
the sandbox. The first meaningful ticket should be small and closely reviewed.
The worker uses `codex exec --approve-for-me`, which selects workspace-write
itself. Do not combine it with `--sandbox`; the CLI rejects that combination
before implementation begins.

Every ticket gets a `codex/issue-NUMBER` branch from fetched main, an isolated
worktree, and a fresh `codex exec` process. Workers cannot publish by instruction;
the dispatcher owns push and draft PR creation. This is a workflow boundary,
not credential isolation from code running under your local user account.
Policy, automation, and workflow edits require supervised work.
The publication guard protects the substantive contribution, development,
release and repository-settings guides, scoped agent instructions, and scripts
outside `scripts/tests/`, including the verification entrypoint. Behavioral tests
under `scripts/tests/` remain available to implementation workers.

The dispatcher runs the final build-and-test command independently inside
`codex sandbox`, with workspace write limits, credential-file read exclusions,
and a minimal environment without publishing tokens. Network access remains
enabled for restore and VSTest's raw TCP connection to its loopback test host.
This profile restricts filesystem access, not network destinations; never expose
live Jellyfin credentials to this checkout. Missing sandbox support blocks
publication instead of falling back to unrestricted execution. Workers run
applicable disposable Jellyfin smoke and lifecycle checks and report outcomes.
The existing PR CI remains required. Tests and local scopes cannot prove the
implementation is correct; a maintainer reviews the draft and merges it.

## Limits and recovery

One local process lock covers the entire worker and verification run. Do not run
a second dispatcher in an independent clone or on another computer: this design
provides single-host coordination, not a distributed lock. The scheduled control
worktree and all ticket worktrees share the original clone's Git common directory.
Do not run manual Jellyfin integration checks while a worker uses the shared
disposable server.

The review backlog limit is two open `codex/issue-*` PRs. Each invocation starts
at most one ticket. Worker time is capped at 60 minutes, final verification at
20 minutes. Failed checks, permission failures and ambiguous scope block the
attempt. There is no automatic retry or unbounded repair loop. Workers may fix
checks within their 60-minute run. Pausing the scheduled task stops future pickup;
interrupt the active dispatcher separately to stop work already underway.

State lives at `<git-common-dir>/agent-dispatch/`, outside the public tree:

- `approvals.json` stores approved ticket content hashes.
- `runs.json` stores branch, base, worktree, status, PID and PR or failure details.
- `runs/NUMBER/` stores the ticket snapshot, worker result and logs.
- `worktrees/NUMBER/` preserves implementation work for recovery.

Run `status` before recovering a failed or interrupted attempt. Check for living
worker processes and an existing remote branch or PR. A stale `running` record
halts the queue even after its process lock is released. Do not delete the lock
file to bypass a live lock. Finish or stop the surviving work first. Then update
that record to `blocked` under the lock, and remove `agent:running` on GitHub.
Existing blocked attempts never restart automatically; continue their preserved
work manually. Keep the record to prevent duplicate branches and PRs.

If PR creation succeeds but a later label update fails, the saved record retains
the PR URL. If the response is lost, search the saved branch on GitHub before
retrying publication. Never force-push or delete a worktree as automatic recovery.
Use a new, explicitly approved follow-up ticket when a fresh attempt is needed.

Notify only for a new draft PR, a failure, a changed approved scope, or required
input. Empty queues, active workers and unchanged review backlogs stay quiet.

## PR review pickup

The implementation dispatcher does not review open PRs. A separate local Codex
heartbeat, `Review Jellyfin PRs`, checks this repository every 30 minutes and
includes both draft and ready PRs. Draft status does not prevent review. The
pickup invokes the installed `code-review` skill, which uses separate fresh
Standards and Spec reviewers. It reviews at most one PR per invocation, oldest
unreviewed first, against its pinned base and head commits.

Review state and reports live under `<git-common-dir>/agent-review/`. Completed
reviews are keyed by PR number, base commit and head commit, so an unchanged
comparison is skipped and new commits become eligible again. Failures are
recorded separately and require attention rather than an automatic retry loop.
Reviewers use isolated detached worktrees and never switch the user's checkout.
They read the linked issue for the Spec axis, or report that no spec is available.

Reports remain local and appear in the heartbeat's chat with separate Standards
and Spec results and an independent impact assessment. The pickup never posts
GitHub comments or reviews, changes code, approves, merges, or releases. Notify
only for a completed review, a new failure, or required input; empty queues and
unchanged failures stay quiet. Pause `Review Jellyfin PRs` to stop future pickup.
