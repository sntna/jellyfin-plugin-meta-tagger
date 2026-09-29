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
             "CONTRIBUTING.md", ".codex/", ".agents/", ".env")
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
    # Inherited by the worker and its child commands. A dead dispatcher cannot
    # release the lock while a surviving worker still accesses the shared server.
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
    if blockers.lower() != "none" and not re.fullmatch(r"[\s,#\d*\-]+", blockers):
        raise ValueError(f"Ticket #{number}: use only #NUMBER references or None for blockers")
    references = {int(n) for n in re.findall(r"#(\d+)", blockers)}
    if blockers.lower() != "none" and not references:
        raise ValueError("Invalid Blocked by section")
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


def eligible(issue, approved, prs, runs):
    labels = {label["name"] for label in issue["labels"]}
    number = issue["number"]
    if issue["state"] != "open" or "pull_request" in issue or issue.get("assignees"):
        return False
    if "agent:ready" not in labels or labels & EXCLUDED:
        return False
    if approved.get(str(number)) != fingerprint(issue):
        return False
    if str(number) in runs:
        return False  # Never automatically retry or overwrite an earlier attempt.
    return not any(pr["headRefName"] == f"codex/issue-{number}" or
                   any(ref["url"] == issue["html_url"] for ref in pr["closingIssuesReferences"])
                   for pr in prs)


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


def run_process(args, cwd, log, lock_fd, seconds, stdin=None):
    with log.open("w") as output:
        process = subprocess.Popen(args, cwd=cwd, stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
            stdout=output, stderr=subprocess.STDOUT, text=True, start_new_session=True,
            pass_fds=(lock_fd,))
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


def implement(issue, root, state, records, fd):
    number = str(issue["number"])
    branch = f"codex/issue-{number}"
    run = state / "runs" / number
    run.mkdir(parents=True, exist_ok=False)
    worktree = state / "worktrees" / number
    base = command(["git", "rev-parse", "origin/main"], root)
    record = {"status": "running", "branch": branch, "worktree": str(worktree),
              "started": time.time(), "base": base, "pid": os.getpid()}
    records[number] = record
    save(state / "runs.json", records)
    try:
        edit(number, "agent:running", ("agent:ready",))
        command(["git", "worktree", "add", "-b", branch, str(worktree), base], root)
        save(run / "ticket.json", issue)
        save(run / "schema.json", SCHEMA)
        prompt = (root / "docs/agents/worker.md").read_text()
        prompt += f"\nBase commit: {base}\nTicket requirements JSON:\n{json.dumps(issue)}"
        run_process(["codex", "exec", "--approve-for-me", "--sandbox", "workspace-write",
                     "-C", str(worktree), "--output-schema", str(run / "schema.json"),
                     "-o", str(run / "result.json"), "--json", "-"],
                    worktree, run / "worker.log", fd, 3600, prompt)
        result = json.loads((run / "result.json").read_text())
        if result["status"] != "ready":
            raise RuntimeError(result["summary"])
        if command(["git", "branch", "--show-current"], worktree) != branch:
            raise RuntimeError("Worker changed branch")
        if command(["git", "status", "--porcelain"], worktree):
            raise RuntimeError("Worker left uncommitted files")
        paths = command(["git", "diff", "--name-only", base, "HEAD"], worktree).splitlines()
        if not paths or any(path.startswith(PROTECTED) for path in paths):
            raise RuntimeError("Empty change or protected automation/policy path changed")
        verified_head = command(["git", "rev-parse", "HEAD"], worktree)
        run_process(["./scripts/build-and-test.sh"], worktree, run / "verify.log", fd, 1200)
        if (command(["git", "status", "--porcelain"], worktree) or
                command(["git", "rev-parse", "HEAD"], worktree) != verified_head):
            raise RuntimeError("Verification changed the working tree or commit")
        current = snapshot(int(number))
        current_labels = {label["name"] for label in current["labels"]}
        if (fingerprint(current) != fingerprint(issue) or current["state"] != "open" or
                "agent:running" not in current_labels or current_labels & (EXCLUDED - {"agent:running"})):
            raise RuntimeError("Ticket approval or scope changed during implementation")
        title = command(["git", "log", "-1", "--format=%s"], worktree)
        if not re.match(r"^(feat|fix|docs|test|chore|refactor|perf|build|ci)(\([^\n]+\))?!?: .+", title):
            raise RuntimeError("Commit subject must use Conventional Commit syntax")
        body = run / "pr.md"
        body.write_text(f"Closes #{number}\n\n{result['summary']}\n\n"
            f"Verification: dispatcher reran `./scripts/build-and-test.sh` successfully.\n\n"
            f"Worker verification report:\n{result['verification']}\n\n"
            "Agent-authored draft. A maintainer must review and merge.\n")
        command(["git", "push", "origin", f"HEAD:refs/heads/{branch}"], worktree)
        url = command(["gh", "pr", "create", "--repo", REPO, "--base", "main", "--head", branch,
                       "--draft", "--title", title, "--body-file", str(body)], worktree)
        record.update(status="review", pr=url)
        save(state / "runs.json", records)  # Save PR before attempting the label update.
        edit(number, "agent:review", ("agent:running",))
        print(json.dumps({"status": "review", "ticket": number, "pr": url}))
    except BaseException as error:
        record.update(status="blocked", error=str(error))
        save(state / "runs.json", records)
        try:
            edit(number, "agent:blocked", ("agent:running", "agent:ready"))
        except Exception:
            pass  # Saved local state still prevents redispatch if GitHub is unavailable.
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["labels", "approve", "status", "dry-run", "run"])
    parser.add_argument("number", nargs="?", type=int)
    args = parser.parse_args()
    root = Path(command(["git", "rev-parse", "--show-toplevel"]))
    common = Path(command(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]))
    state = common / "agent-dispatch"
    state.mkdir(exist_ok=True)
    (state / "worktrees").mkdir(exist_ok=True)
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
        command(["git", "fetch", "origin", "main"], root)
        if any(run["status"] == "running" for run in records.values()):
            raise RuntimeError("Unfinished run exists; inspect saved state and surviving processes")
        prs = pull_requests("open")
        if sum(pr["headRefName"].startswith("codex/issue-") for pr in prs) >= 2:
            print('{"status":"review-limit"}')
            return
        candidates = flatten(f"repos/{REPO}/issues?state=open&labels=agent%3Aready&per_page=100")
        selected = []
        skipped = []
        merged = None
        for candidate in candidates:
            if str(candidate["number"]) not in approvals:
                continue
            try:
                issue = snapshot(candidate["number"])
            except ValueError as error:
                skipped.append(str(error))
                continue
            if not eligible(issue, approvals, prs, records):
                if approvals.get(str(issue["number"])) != fingerprint(issue):
                    skipped.append(f"Ticket #{issue['number']}: approved scope changed; review and approve again")
                continue
            if issue["blockers"] and merged is None:
                merged = pull_requests("merged")
            if blockers_complete(issue, merged or [], root):
                selected.append(issue)
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
        implement(issue, root, state, records, fd)


if __name__ == "__main__":
    try:
        main()
    except BlockingIOError:
        print('{"status":"busy"}')
    except Exception as exc:
        print(json.dumps({"status": "error", "error": str(exc)}), file=sys.stderr)
        sys.exit(1)
