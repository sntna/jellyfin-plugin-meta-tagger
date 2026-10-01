#!/usr/bin/env python3
"""Single-host, serialized GitHub ticket dispatcher. No third-party dependencies."""

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import uuid


REPO = "sntna/jellyfin-plugin-meta-tagger"
LABELS = {
    "needs-triage": "Needs maintainer scope review",
    "needs-info": "Waiting for information",
    "ready-for-human": "Requires supervised implementation",
    "agent:ready": "Approved implementation ticket",
    "agent:running": "Local implementation is in progress",
    "agent:review": "Draft implementation PR awaits maintainer review",
    "agent:blocked": "Implementation needs maintainer attention",
}
EXCLUDED = {"needs-triage", "needs-info", "ready-for-human", "wontfix",
            "agent:running", "agent:review", "agent:blocked"}
PROTECTED = (".github/", "docs/agents/", "scripts/agent-dispatch.py", "AGENTS.md",
             "CONTRIBUTING.md", "docs/contributing.md", "docs/development.md",
             "docs/releasing.md", "docs/release-builds.md", "docs/github-settings.md",
             ".codex/", ".agents/", ".env")
SCHEMA = {"type": "object", "properties": {
    "status": {"type": "string", "enum": ["ready", "blocked"]},
    "summary": {"type": "string"}, "verification": {"type": "string"}},
    "required": ["status", "summary", "verification"], "additionalProperties": False}


def command(args, cwd=None, timeout=120):
    return subprocess.run(args, cwd=cwd, check=True, capture_output=True,
                          text=True, timeout=timeout).stdout.strip()


def api(path):
    return json.loads(command(["gh", "api", path]))


def pages(path):
    return json.loads(command(["gh", "api", "--paginate", "--slurp", path]))


def flatten(path):
    return [item for page in pages(path) for item in page]


def save(path, value):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(value, indent=2) + "\n")
    temp.replace(path)


@contextlib.contextmanager
def lock(path):
    # Serialize state transitions. Between app turns, the durable running record
    # prevents another claim. Verification children also inherit this lock.
    with path.open("a+") as handle:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield handle.fileno()


def section(body, name):
    match = re.search(r"^#{2,3} " + re.escape(name) + r"\s*\n(.*?)(?=^#{2,3} |\Z)",
                      body, re.M | re.S | re.I)
    return match.group(1).strip() if match else ""


def snapshot(number):
    issue = api(f"repos/{REPO}/issues/{number}")
    native = flatten(f"repos/{REPO}/issues/{number}/dependencies/blocked_by?per_page=100")
    body = issue.get("body") or ""
    blockers = section(body, "Blocked by")
    if not blockers:
        raise ValueError(f"Ticket #{number} needs a Blocked by section")
    references = set()
    if blockers.lower() != "none":
        for line in blockers.splitlines():
            line = line.strip()
            if not line:
                continue
            if not re.fullmatch(r"(?:[-*][ \t]+)?#[1-9][0-9]*(?:(?:[ \t]*,[ \t]*|[ \t]+)#[1-9][0-9]*)*", line):
                raise ValueError(f"Ticket #{number}: use only #NUMBER references or None for blockers")
            references.update(int(n) for n in re.findall(r"#([1-9][0-9]*)", line))
    for blocker in native:
        if not blocker["url"].startswith(f"https://api.github.com/repos/{REPO}/issues/"):
            raise ValueError("Cross-repository dependencies require supervised work")
        references.add(blocker["number"])
    if number in references:
        raise ValueError("A ticket cannot block itself")
    for name in ("In scope", "Out of scope", "Acceptance criteria"):
        if not section(body, name):
            raise ValueError(f"Ticket #{number} needs {name}")
    if not (section(body, "Objective") or section(body, "What to build")):
        raise ValueError(f"Ticket #{number} needs an objective")
    priority = section(body, "Priority").lower()
    if priority not in {"high", "normal", "low"}:
        raise ValueError(f"Ticket #{number} needs High, Normal, or Low priority")
    issue["blockers"] = sorted(references)
    issue["priority"] = {"high": 0, "normal": 1, "low": 2}[priority]
    return issue


def fingerprint(issue):
    content = [issue["title"], issue.get("body"), issue["blockers"]]
    return hashlib.sha256(json.dumps(content, sort_keys=True).encode()).hexdigest()


def has_implementation_pr(issue, prs):
    return any(pr["headRefName"] == f"codex/issue-{issue['number']}" or
               any(ref["url"] == issue["html_url"] for ref in pr["closingIssuesReferences"])
               for pr in prs)


def ticket_is_available(issue, approved_fingerprint, expected_label):
    labels = {label["name"] for label in issue["labels"]}
    return (issue["state"] == "open" and "pull_request" not in issue and not issue.get("assignees") and
            expected_label in labels and not labels & (EXCLUDED - {expected_label}) and
            approved_fingerprint == fingerprint(issue))


def ineligibility_reason(issue, approved, prs, runs):
    number = issue["number"]
    if str(number) not in approved:
        return "no local approval snapshot; labels alone do not authorize execution"
    if approved[str(number)] != fingerprint(issue):
        return "approved scope changed; review and approve again"
    if issue["state"] != "open" or "pull_request" in issue:
        return "not an open implementation issue"
    if issue.get("assignees"):
        return "already assigned"
    labels = {label["name"] for label in issue["labels"]}
    if labels & EXCLUDED:
        return "excluded queue labels: " + ", ".join(sorted(labels & EXCLUDED))
    if "agent:ready" not in labels:
        return "missing agent:ready"
    if str(number) in runs:
        return f"saved {runs[str(number)]['status']} attempt; inspect preserved work manually"
    if has_implementation_pr(issue, prs):
        return "an implementation PR already exists"
    return None


def eligible(issue, approved, prs, runs):
    return ineligibility_reason(issue, approved, prs, runs) is None


def edit(number, add, remove=()):
    args = ["gh", "issue", "edit", str(number), "--repo", REPO, "--add-label", add]
    for label in remove:
        args.extend(["--remove-label", label])
    command(args)


def pull_requests(state):
    prs = json.loads(command(["gh", "pr", "list", "--repo", REPO, "--state", state,
        "--limit", "1000", "--json", "number,url,headRefName,baseRefName,closingIssuesReferences,mergeCommit"]))
    if len(prs) == 1000:
        raise RuntimeError("PR history exceeds dispatcher limit; inspect before increasing it")
    return prs


def blockers_complete(issue, merged, root):
    for number in issue["blockers"]:
        blocker = api(f"repos/{REPO}/issues/{number}")
        if blocker["state"] != "closed" or blocker.get("state_reason") != "completed":
            return False
        closing = [pr for pr in merged if pr["baseRefName"] == "main" and pr["mergeCommit"] and
                   any(ref["url"] == blocker["html_url"] for ref in pr["closingIssuesReferences"])]
        if not closing:
            return False
        if not any(subprocess.run(["git", "merge-base", "--is-ancestor", pr["mergeCommit"]["oid"],
                                   "origin/main"], cwd=root, capture_output=True).returncode == 0
                   for pr in closing):
            return False
    return True


def run_process(args, cwd, log, lock_fd, seconds, stdin=None, env=None):
    with log.open("w") as output:
        process = subprocess.Popen(args, cwd=cwd, stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
            stdout=output, stderr=subprocess.STDOUT, text=True, start_new_session=True,
            pass_fds=(lock_fd,), env=env)
        try:
            process.communicate(stdin, timeout=seconds)
            if process.returncode:
                raise RuntimeError(f"Command failed ({process.returncode}); see {log}")
        finally:
            # Stop descendants too, including a worker abandoned by a timed-out command.
            try:
                os.killpg(process.pid, signal.SIGTERM)
                time.sleep(0.2)
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()


def verification_command(worktree):
    # Keep generated test/build code within an explicit execution boundary, even
    # when the trusted dispatcher itself runs from an unrestricted local terminal.
    settings = [
        'permissions.agent-verify.extends=":workspace"',
        'permissions.agent-verify.network.enabled=true',
        # VSTest uses raw TCP to its loopback test host, not an HTTP/SOCKS proxy.
        'features.network_proxy=false',
    ]
    denied = ("~/.ssh", "~/.config/gh", "~/.codex/auth.json", "~/Library/Keychains")
    settings.append("permissions.agent-verify.filesystem={" +
                    ",".join(f'{json.dumps(path)}="deny"' for path in denied) + "}")
    args = ["codex", "sandbox", "-P", "agent-verify", "-C", str(worktree)]
    for setting in settings:
        args.extend(["-c", setting])
    return args + ["--", "./scripts/build-and-test.sh"]


def review_limit(prs):
    return sum(pr["headRefName"].startswith("codex/issue-") for pr in prs) >= 2


def protected_path(path):
    # Tests remain an implementation surface; executable automation and scoped
    # agent instructions require a supervised session.
    return (path.startswith(PROTECTED) or Path(path).name in {"AGENTS.md", "AGENTS.override.md"} or
            (path.startswith("scripts/") and not path.startswith("scripts/tests/")))


def mark_blocked(number, state, records, reason):
    record = records[str(number)]
    record.update(status="blocked", error=reason)
    save(state / "runs.json", records)
    try:
        edit(number, "agent:blocked", ("agent:running", "agent:ready"))
    except Exception:
        pass  # Saved state prevents redispatch even if GitHub is unavailable.


def claim(issue, root, state, records):
    number = str(issue["number"])
    if any(record["status"] == "running" for record in records.values()):
        raise RuntimeError("Unfinished run exists; inspect saved state and active app sessions")
    if number in records:
        raise RuntimeError("Prior run exists; inspect preserved work manually")
    # Place app-editable files inside the project's writable tree, outside .git.
    # The private approvals and run history stay in the shared Git directory.
    if state.parent.name != ".git":
        raise RuntimeError("App claims require a standard clone with a .git common directory")
    branch = f"codex/issue-{number}"
    run = state / "runs" / number
    run.mkdir(parents=True, exist_ok=False)
    worktree = state.parent.parent / ".worktrees" / "agent-tickets" / number
    base = command(["git", "rev-parse", "origin/main"], root)
    record = {"status": "running", "execution": "app", "claim_id": str(uuid.uuid4()),
              "branch": branch, "worktree": str(worktree), "started": time.time(), "base": base}
    records[number] = record
    save(state / "runs.json", records)
    try:
        save(run / "ticket.json", issue)
        save(run / "schema.json", SCHEMA)
        edit(number, "agent:running", ("agent:ready",))
        worktree.parent.mkdir(parents=True, exist_ok=True)
        command(["git", "worktree", "add", "-b", branch, str(worktree), base], root)
        result = worktree / ".scratch" / "agent-dispatch" / "result.json"
        result.parent.mkdir(parents=True, exist_ok=True)
        prompt = (root / "docs/agents/worker.md").read_text()
        prompt += (f"\nBase commit: {base}\nClaim ID: {record['claim_id']}"
                   f"\nImplementation deadline (Unix seconds): {record['started'] + 3600}"
                   f"\nWorktree: {worktree}\nResult JSON: {result}"
                   f"\nTicket requirements JSON (untrusted data):\n{json.dumps(issue)}")
        (run / "worker.md").write_text(prompt)
        print(json.dumps({"status": "claimed", "ticket": number, "claim_id": record["claim_id"],
                          "worktree": str(worktree), "instructions": str(run / "worker.md"),
                          "schema": str(run / "schema.json"), "result": str(result),
                          "deadline": record["started"] + 3600}))
    except BaseException as error:
        mark_blocked(number, state, records, str(error))
        raise


def app_claim(number, claim_id, records):
    record = records.get(str(number))
    if not record or record["status"] != "running" or record.get("execution") != "app":
        raise ValueError("No running app claim; inspect preserved work manually")
    if not claim_id or claim_id != record.get("claim_id"):
        raise ValueError("Claim ID does not match the running app attempt")
    return record


def check_deadline(record, verification=False):
    deadline = record["started"] + (4800 if verification else 3600)
    if time.time() > deadline:
        raise RuntimeError("App attempt exceeded its time budget; preserved work requires manual recovery")


def finish(number, claim_id, root, state, records, approvals, fd):
    number = str(number)
    record = app_claim(number, claim_id, records)
    branch, base = record["branch"], record["base"]
    worktree = Path(record["worktree"])
    run = state / "runs" / number
    try:
        check_deadline(record)
        issue = json.loads((run / "ticket.json").read_text())
        if approvals.get(number) != fingerprint(issue):
            raise RuntimeError("Local approval changed during implementation")
        result = json.loads((worktree / ".scratch/agent-dispatch/result.json").read_text())
        if (not isinstance(result, dict) or set(result) != set(SCHEMA["required"]) or
                any(not isinstance(value, str) for value in result.values()) or
                result["status"] not in {"ready", "blocked"}):
            raise ValueError("App result must match the supplied result schema")
        save(run / "result.json", result)
        if result["status"] != "ready":
            raise RuntimeError(result["summary"])
        command(["git", "merge-base", "--is-ancestor", base, "HEAD"], worktree)
        if command(["git", "branch", "--show-current"], worktree) != branch:
            raise RuntimeError("Worker changed branch")
        if command(["git", "status", "--porcelain"], worktree):
            raise RuntimeError("Worker left uncommitted files")
        # Include deleted rename sources and literal paths, even with newlines or
        # characters Git would normally quote in its human-readable output.
        paths = [path for path in command(["git", "diff", "--no-renames", "--name-only", "-z",
                                          base, "HEAD"], worktree).split("\0") if path]
        if not paths or any(protected_path(path) for path in paths):
            raise RuntimeError("Empty change or protected automation/policy path changed")
        verified_head = command(["git", "rev-parse", "HEAD"], worktree)
        verify_env = {key: os.environ[key] for key in ("PATH", "HOME", "TMPDIR", "LANG") if key in os.environ}
        verify_env.update(NUGET_PACKAGES=str(worktree / ".nuget/packages"),
                          DOTNET_CLI_HOME=str(worktree / ".scratch/dotnet"),
                          DOTNET_CLI_TELEMETRY_OPTOUT="1")
        run_process(verification_command(worktree), worktree, run / "verify.log", fd, 1200, env=verify_env)
        if (command(["git", "status", "--porcelain"], worktree) or
                command(["git", "rev-parse", "HEAD"], worktree) != verified_head):
            raise RuntimeError("Verification changed the working tree or commit")
        check_deadline(record, verification=True)
        current = snapshot(int(number))
        if not ticket_is_available(current, fingerprint(issue), "agent:running"):
            raise RuntimeError("Ticket approval, assignment or scope changed during implementation")
        if current["blockers"]:
            command(["git", "fetch", "origin", "main"], root)
            if not blockers_complete(current, pull_requests("merged"), root):
                raise RuntimeError("Ticket dependencies changed during implementation")
        title = command(["git", "log", "-1", "--format=%s"], worktree)
        if not re.match(r"^(feat|fix|docs|test|chore|refactor|perf|build|ci)(\([^\n]+\))?!?: .+", title):
            raise RuntimeError("Commit subject must use Conventional Commit syntax")
        body = run / "pr.md"
        body.write_text(f"Closes #{number}\n\n{result['summary']}\n\n"
            f"Verification: dispatcher reran `./scripts/build-and-test.sh` successfully.\n\n"
            f"Worker verification report:\n{result['verification']}\n\n"
            "Agent-authored draft. A maintainer must review and merge.\n")
        prs = pull_requests("open")
        if has_implementation_pr(issue, prs):
            raise RuntimeError("Another implementation PR appeared; verified work preserved")
        if review_limit(prs):
            raise RuntimeError("Review backlog filled during implementation; verified work preserved")
        check_deadline(record, verification=True)
        command(["git", "push", "origin", f"HEAD:refs/heads/{branch}"], worktree)
        url = command(["gh", "pr", "create", "--repo", REPO, "--base", "main", "--head", branch,
                       "--draft", "--title", title, "--body-file", str(body)], worktree)
        record.update(status="review", pr=url)
        save(state / "runs.json", records)  # Save PR before attempting the label update.
        edit(number, "agent:review", ("agent:running",))
        print(json.dumps({"status": "review", "ticket": number, "pr": url}))
    except BaseException as error:
        mark_blocked(number, state, records, str(error))
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["labels", "approve", "status", "dry-run", "claim", "finish", "block"])
    parser.add_argument("number", nargs="?", type=int)
    parser.add_argument("--claim-id")
    parser.add_argument("--reason", help="Concrete reason an app attempt cannot finish")
    args = parser.parse_args()
    if args.action in {"finish", "block"} and (not args.number or not args.claim_id):
        parser.error("finish and block require a ticket number and --claim-id")
    if args.action == "block" and not args.reason:
        parser.error("block requires --reason")
    root = Path(command(["git", "rev-parse", "--show-toplevel"]))
    common = Path(command(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]))
    state = common / "agent-dispatch"
    state.mkdir(exist_ok=True)
    with lock(state / "lock") as fd:
        approvals_file = state / "approvals.json"
        approvals = json.loads(approvals_file.read_text()) if approvals_file.exists() else {}
        records_file = state / "runs.json"
        records = json.loads(records_file.read_text()) if records_file.exists() else {}
        if args.action == "status":
            print(json.dumps({"approved": list(approvals), "runs": records, "state": str(state)}, indent=2))
            return
        if args.action == "labels":
            for label, description in LABELS.items():
                command(["gh", "label", "create", label, "--repo", REPO,
                         "--description", description, "--color", "5319e7", "--force"])
            return
        if args.action == "approve":
            if not args.number:
                parser.error("approve requires a ticket number")
            permission = api(f"repos/{REPO}")["permissions"]
            if not permission.get("push"):
                raise RuntimeError("Approval requires repository write permission")
            issue = snapshot(args.number)
            if issue["state"] != "open" or "pull_request" in issue:
                raise ValueError("Only open implementation tickets can be approved")
            if str(args.number) in records:
                raise ValueError("Prior run exists; inspect saved work before manual recovery")
            approvals[str(args.number)] = fingerprint(issue)
            save(approvals_file, approvals)
            edit(args.number, "agent:ready", ("needs-triage", "needs-info", "ready-for-human", "agent:blocked"))
            print(f"Approved ticket #{args.number}")
            return
        if args.action == "block":
            app_claim(args.number, args.claim_id, records)
            mark_blocked(args.number, state, records, args.reason)
            print(json.dumps({"status": "blocked", "ticket": args.number, "error": args.reason}))
            return
        if args.action == "finish":
            finish(args.number, args.claim_id, root, state, records, approvals, fd)
            return
        active = {number: run for number, run in records.items() if run["status"] == "running"}
        if active:
            if all(run.get("execution") == "app" and time.time() <= run["started"] + 4800
                   for run in active.values()):
                print(json.dumps({"status": "busy", "tickets": list(active)}))
                return
            raise RuntimeError("Unfinished run exists; inspect saved state and active app sessions")
        command(["git", "fetch", "origin", "main"], root)
        prs = pull_requests("open")
        if review_limit(prs):
            print('{"status":"review-limit"}')
            return
        candidates = flatten(f"repos/{REPO}/issues?state=open&labels=agent%3Aready&per_page=100")
        selected = []
        skipped = []
        merged = None
        for candidate in candidates:
            if str(candidate["number"]) not in approvals:
                skipped.append(f"Ticket #{candidate['number']}: no local approval snapshot; labels alone do not authorize execution")
                continue
            try:
                issue = snapshot(candidate["number"])
            except ValueError as error:
                skipped.append(str(error))
                continue
            reason = ineligibility_reason(issue, approvals, prs, records)
            if reason:
                skipped.append(f"Ticket #{issue['number']}: {reason}")
                continue
            if issue["blockers"] and merged is None:
                merged = pull_requests("merged")
            if blockers_complete(issue, merged or [], root):
                selected.append(issue)
            else:
                references = ", ".join(f"#{number}" for number in issue["blockers"])
                skipped.append(f"Ticket #{issue['number']}: dependencies are not complete in main; check {references}")
        selected.sort(key=lambda issue: (issue["priority"], issue["created_at"], issue["number"]))
        if args.action == "dry-run" or not selected:
            print(json.dumps({"status": "eligible" if selected else "idle",
                              "tickets": [issue["number"] for issue in selected], "skipped": skipped}))
            return
        for skill in ("implement", "tdd", "code-review"):
            if not (Path.home() / ".agents/skills" / skill / "SKILL.md").exists():
                raise RuntimeError(f"Missing required local skill: {skill}")
        # Re-read scope and queue state under the host-wide lock immediately before claiming.
        issue = snapshot(selected[0]["number"])
        if not eligible(issue, approvals, pull_requests("open"), records):
            raise RuntimeError("Ticket eligibility changed before claim")
        if not blockers_complete(issue, merged or [], root):
            raise RuntimeError("Ticket dependencies changed before claim")
        claim(issue, root, state, records)


if __name__ == "__main__":
    try:
        main()
    except BlockingIOError:
        print('{"status":"busy"}')
    except Exception as exc:
        print(json.dumps({"status": "error", "error": str(exc)}), file=sys.stderr)
        sys.exit(1)
