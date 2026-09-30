# Issue tracker

Use GitHub Issues in `sntna/jellyfin-plugin-meta-tagger`, through `gh`.
A ticket is a bounded unit of work; its GitHub Issue is the stored record.
Specs can also be issues, but only implementation tickets enter the worker queue.
PRs as a request surface: no.

Read a ticket with `gh issue view NUMBER --comments`. Publish one issue per
ticket with `gh issue create --body-file FILE`. Use files for multiline bodies.
Never interpolate issue text into shell commands.

`/to-tickets` publishes the user-approved breakdown here, in dependency order.
Include Objective or What to build, In scope, Out of scope, Acceptance criteria,
Blocked by, and Priority sections. Use High, Normal, or Low priority.
Blocked by must contain only same-repository `#NUMBER` references or `None`.
Add native GitHub dependencies too, using the blocker's database ID:

```sh
gh api --method POST repos/sntna/jellyfin-plugin-meta-tagger/issues/CHILD/dependencies/blocked_by -F issue_id=BLOCKER_DATABASE_ID
```

After publishing the breakdown the user has approved, run
`python3 scripts/agent-dispatch.py approve NUMBER` for each implementation ticket.
This applies `agent:ready`, removes triage labels, and records the approved title,
body and dependency snapshot locally. No additional triage is needed. Do not
approve new scope the user has not agreed to. Do not close or modify the parent.

Incoming reports go through `/triage`; a maintainer reviews their resulting
implementation scope before running the same approval command. A label alone
does not authorize local execution. Editing scope or dependencies invalidates
the snapshot; approve the revised ticket after review.

The dispatcher considers both native dependencies and the Blocked by section.
Each blocker must be closed as completed and have a merged closing PR targeting
main whose merge commit is in the fetched main branch. Decision-only work should
be resolved in the spec before publishing implementation tickets.
