#!/usr/bin/env python3
"""Check or squash-merge an approved ticket after independent review and CI."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys

spec = importlib.util.spec_from_file_location("dispatcher", Path(__file__).with_name("agent-dispatch.py"))
dispatcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dispatcher)
REPO = dispatcher.REPO
REQUIRED_CHECKS = {"verify", "analyze-csharp", "pr-title"}


def validate_review(review, pr, approval):
    if (review.get("repository") != REPO or review.get("pr") != pr["number"]
            or review.get("base") != pr["base"]["sha"] or review.get("head") != pr["head"]["sha"]
            or review.get("approval") != approval or review.get("status") != "passed"):
        raise ValueError("Review does not approve this exact PR, base, head and ticket scope")
    reviewers = []
    for axis in ("standards", "spec"):
        result = review.get(axis, {})
        if (result.get("status") != "passed" or result.get("findings") != []
                or not isinstance(result.get("reviewer"), str) or not result["reviewer"].strip()
                or not isinstance(result.get("report"), str) or not result["report"].strip()):
            raise ValueError("Both independent review axes must pass with reports and no unresolved findings")
        reviewers.append(result["reviewer"])
    if reviewers[0] == reviewers[1]:
        raise ValueError("Standards and Spec require different fresh reviewers")
    verification = review.get("verification", {})
    if (verification.get("head") != pr["head"]["sha"] or verification.get("status") != "passed"
            or not verification.get("report")):
        raise ValueError("Successful verification evidence must match the reviewed head")


def validate_pr(pr, issue, approved, merge_ready=True):
    if (pr["state"] != "open" or pr["base"]["ref"] != "main"
            or pr["base"]["repo"]["full_name"] != REPO
            or pr["head"]["repo"]["full_name"] != REPO
            or pr["user"]["login"].lower() != "sntna"
            or pr["head"]["ref"] != f"codex/issue-{issue['number']}"):
        raise ValueError("Only same-repository approved ticket PRs by sntna targeting main may merge")
    reason = dispatcher.availability_reason(issue, approved, "agent:review")
    if reason:
        raise ValueError(reason)
    if merge_ready and (pr.get("mergeable") is not True or pr.get("mergeable_state") != "clean"):
        raise ValueError("PR must be conflict-free, current with main, and allowed by GitHub rules")


def validate_checks(checks, required):
    if not REQUIRED_CHECKS.issubset({check["name"] for check in required}):
        raise ValueError("Expected required GitHub checks are missing")
    if any(check["bucket"] != "pass" for check in required):
        raise ValueError("Every required check must pass; pending and skipped checks do not pass")
    if any(check["bucket"] not in {"pass", "skipping"} for check in checks):
        raise ValueError("A GitHub check is pending, cancelled or failing")


def approved_ticket(number, state, merge_ready=True):
    pr = dispatcher.api(f"repos/{REPO}/pulls/{number}")
    match = re.fullmatch(r"codex/issue-([1-9][0-9]*)", pr["head"]["ref"])
    if not match:
        raise ValueError("PR does not belong to an approved implementation ticket")
    issue = dispatcher.snapshot(int(match[1]))
    approvals = json.loads((state / "approvals.json").read_text())
    approval = approvals.get(str(issue["number"]))
    validate_pr(pr, issue, approval, merge_ready)
    records = json.loads((state / "runs.json").read_text())
    record = records.get(str(issue["number"]))
    if not record or record.get("branch") != pr["head"]["ref"]:
        raise ValueError("Missing matching implementation claim and path-permission snapshot")
    return pr, issue, approval, record


def check_remote_paths(number, pr, state, issue, record):
    files = dispatcher.flatten(f"repos/{REPO}/pulls/{number}/files?per_page=100")
    if len(files) != pr.get("changed_files") or len(files) >= 3000:
        raise ValueError("Complete PR changed-file list is unavailable")
    paths = [file[key] for file in files for key in ("filename", "previous_filename") if key in file]
    dispatcher.validate_publication_paths(paths, state, issue, record)
    latest = dispatcher.api(f"repos/{REPO}/pulls/{number}")
    if any(latest[side]["sha"] != pr[side]["sha"] for side in ("head", "base")):
        raise ValueError("PR revisions changed while reading changed paths")


def check_repair_paths(number, worktree, state):
    pr, issue, _, record = approved_ticket(number, state, merge_ready=False)
    if (dispatcher.command(["git", "branch", "--show-current"], worktree) != pr["head"]["ref"]
            or dispatcher.command(["git", "status", "--porcelain"], worktree)):
        raise ValueError("Repair must be committed on the PR branch with a clean working tree")
    dispatcher.command(["git", "merge-base", "--is-ancestor", pr["head"]["sha"], "HEAD"], worktree)
    paths = [path for path in dispatcher.command(
        ["git", "diff", "--no-renames", "--name-only", "-z", f"{pr['base']['sha']}...HEAD"], worktree).split("\0") if path]
    dispatcher.validate_publication_paths(paths, state, issue, record)
    return dispatcher.command(["git", "rev-parse", "HEAD"], worktree)


def check(number, review, state):
    pr, issue, approval, record = approved_ticket(number, state)
    validate_review(review, pr, approval)
    check_remote_paths(number, pr, state, issue, record)
    if dispatcher.api(f"repos/{REPO}/commits/main")["sha"] != review["base"]:
        raise ValueError("Main changed after review; update the branch and repeat review")
    args = ["gh", "pr", "checks", str(number), "--repo", REPO, "--json", "name,bucket"]
    required = json.loads(dispatcher.command(args + ["--required"]))
    checks = json.loads(dispatcher.command(args))
    validate_checks(checks, required)
    return pr


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["check", "check-paths", "merge"])
    parser.add_argument("number", type=int)
    parser.add_argument("--review", type=Path)
    parser.add_argument("--worktree", type=Path)
    args = parser.parse_args()
    if args.action == "check-paths" and not args.worktree:
        parser.error("check-paths requires --worktree")
    if args.action != "check-paths" and not args.review:
        parser.error("check and merge require --review")
    if dispatcher.api("user")["login"].lower() != "sntna":
        raise ValueError("Expected authenticated GitHub account sntna")
    common = Path(dispatcher.command(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]))
    # Share the dispatch lock so approvals and publication cannot change during merge.
    with dispatcher.lock(common / "agent-dispatch/lock"):
        if args.action == "check-paths":
            head = check_repair_paths(args.number, args.worktree, common / "agent-dispatch")
            print(json.dumps({"status": "passed", "pr": args.number, "head": head}))
            return
        review = json.loads(args.review.read_text())
        pr = check(args.number, review, common / "agent-dispatch")
        if args.action == "merge":
            if pr["draft"]:
                raise ValueError("Mark the independently approved draft ready, then rerun all gates")
            dispatcher.command(["gh", "pr", "merge", str(args.number), "--repo", REPO,
                                "--squash", "--match-head-commit", review["head"], "--subject", pr["title"]])
            result = dispatcher.api(f"repos/{REPO}/pulls/{args.number}")
            if not result.get("merged"):
                raise RuntimeError("Merge is not confirmed; inspect GitHub before retrying")
            print(json.dumps({"status": "merged", "pr": args.number, "commit": result["merge_commit_sha"]}))
        else:
            print(json.dumps({"status": "passed", "pr": args.number, "base": review["base"], "head": review["head"]}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"status": "blocked", "error": str(error)}), file=sys.stderr)
        sys.exit(1)
