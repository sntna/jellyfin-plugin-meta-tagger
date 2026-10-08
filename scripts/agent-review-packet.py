#!/usr/bin/env python3
"""Build and validate an offline review handoff. This does not authorize merging."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import uuid


spec = importlib.util.spec_from_file_location("dispatcher", Path(__file__).with_name("agent-dispatch.py"))
dispatcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dispatcher)


def read_json(path):
    return json.loads(Path(path).read_text())


def required_file(path):
    path = Path(path).resolve()
    if not path.is_file():
        raise ValueError(f"Missing evidence file: {path}")
    return path


def copy_file(source, directory, name):
    destination = directory / name
    shutil.copyfile(required_file(source), destination)
    return str(destination)


def evidence_paths(manifest):
    paths = [manifest["requirements"], manifest["diff"], manifest["verification"]["result"],
             manifest["verification"]["metadata"], *manifest["verification"]["browser"]]
    if manifest.get("design"):
        paths.append(manifest["design"]["path"])
    paths.extend(report["path"] for report in manifest["reports"].values())
    return paths


def digest(path):
    with required_file(path).open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def save_manifest(path, manifest):
    manifest["files"] = {path: digest(path) for path in evidence_paths(manifest)}
    dispatcher.save(path, manifest)


def revision(value, root):
    return dispatcher.command(["git", "rev-parse", "--verify", f"{value}^{{commit}}"], root)


def requirements(ticket, number):
    if (ticket.get("number") != number or not ticket.get("title")
            or not isinstance(ticket.get("body"), str) or not isinstance(ticket.get("blockers"), list)):
        raise ValueError("Requirements need the full issue snapshot with number, title, body and blockers")
    for name in ("In scope", "Out of scope", "Acceptance criteria", "Blocked by", "Priority"):
        if not dispatcher.section(ticket["body"], name):
            raise ValueError(f"Requirements need the full issue snapshot, including {name}")
    if not (dispatcher.section(ticket["body"], "Objective") or dispatcher.section(ticket["body"], "What to build")):
        raise ValueError("Requirements need the full issue snapshot, including Objective or What to build")


def build(args):
    root = Path(dispatcher.command(["git", "rev-parse", "--show-toplevel"]))
    common = Path(dispatcher.command(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]))
    remote = dispatcher.command(["git", "remote", "get-url", "origin"], root)
    repository = remote.removesuffix(".git").removeprefix("https://github.com/").removeprefix("git@github.com:")
    if repository != dispatcher.REPO:
        raise ValueError(f"Expected origin for {dispatcher.REPO}; found {remote}")
    if bool(args.ticket) != bool(args.approval):
        raise ValueError("Supervised snapshots require both --ticket and --approval fingerprint")
    if bool(args.design) != bool(args.design_approval and args.design_approval.strip()):
        raise ValueError("Design evidence requires both --design and --design-approval reference")
    ticket_path = required_file(args.ticket or common / "agent-dispatch/runs" / str(args.issue) / "ticket.json")
    ticket = read_json(ticket_path)
    requirements(ticket, args.issue)
    approval_path = common / "agent-dispatch/approvals.json"
    approval = args.approval or read_json(approval_path).get(str(args.issue))
    if approval != dispatcher.fingerprint(ticket):
        raise ValueError("Saved requirements do not match the approved issue fingerprint")
    base, head = revision(args.base, root), revision(args.head, root)
    verification = read_json(required_file(args.verification))
    if verification.get("head") != head:
        raise ValueError(f"Verification head must identify reviewed head {head}")
    if not verification.get("command") or verification.get("status") not in {"passed", "failed"}:
        raise ValueError("Verification needs a command and passed or failed status")
    required_file(verification.get("result", ""))
    browser = verification.get("browser", [])
    if not isinstance(browser, list):
        raise ValueError("Verification browser must be a list of local evidence paths")
    for path in browser:
        required_file(path)
    if args.design:
        required_file(args.design)
    directory = common / "agent-review/packets" / str(uuid.uuid4())
    directory.mkdir(parents=True)
    verification["result"] = copy_file(verification["result"], directory, "verification.log")
    verification["metadata"] = copy_file(args.verification, directory, "verification.json")
    verification["browser"] = [copy_file(path, directory, f"browser-{index}{Path(path).suffix}")
                               for index, path in enumerate(browser)]
    manifest = {"schema": 1, "status": "current", "repository": repository, "remote": remote,
                "root": str(root), "common": str(common), "issue": args.issue, "approval": approval,
                "base": base, "head": head, "base_ref": args.base, "head_ref": args.head,
                "requirements_source": str(ticket_path),
                "approval_source": str(approval_path) if not args.ticket else None,
                "requirements": copy_file(ticket_path, directory, "ticket.json"),
                "verification": verification, "reports": {}}
    if args.design:
        manifest["design"] = {"path": copy_file(args.design, directory, f"design{args.design.suffix}"),
                              "approval": args.design_approval}
    diff = directory / "comparison.diff"
    comparison = subprocess.run(["git", "diff", "--binary", f"{base}...{head}"], cwd=root,
                                check=True, capture_output=True, timeout=dispatcher.COMMAND_TIMEOUT)
    diff.write_bytes(comparison.stdout)
    manifest["diff"] = str(diff)
    path = directory / "manifest.json"
    save_manifest(path, manifest)
    for previous in directory.parent.glob("*/manifest.json"):
        if previous == path:
            continue
        old = read_json(previous)
        if old["issue"] == args.issue and any(old[key] != manifest[key] for key in ("base", "head", "approval")):
            obsolete(previous, old, "A newer packet changes the comparison or approved requirements")
    return {"status": "current", "manifest": str(path), "issue": args.issue, "base": base, "head": head}


def obsolete(path, manifest, reason):
    manifest.update(status="obsolete", obsolete_reason=reason)
    dispatcher.save(path, manifest)


def validate(path, base=None, head=None, approval=None):
    path = required_file(path)
    manifest = read_json(path)
    if manifest["status"] != "current":
        raise ValueError("Packet is obsolete; build a fresh handoff and repeat both reviews")
    root = Path(manifest["root"])
    comparison = {"base": revision(base or manifest["base_ref"], root),
                  "head": revision(head or manifest["head_ref"], root), "approval": approval or manifest["approval"]}
    ticket = read_json(required_file(manifest["requirements_source"]))
    if manifest.get("approval_source"):
        comparison["approval"] = approval or read_json(required_file(manifest["approval_source"])).get(str(manifest["issue"]))
    if (any(comparison[key] != manifest[key] for key in comparison)
            or dispatcher.fingerprint(ticket) != manifest["approval"]):
        obsolete(path, manifest, "Base, head or approved issue fingerprint changed")
        raise ValueError("Packet is obsolete because the base, head or approved issue fingerprint changed; rebuild it")
    requirements(ticket, manifest["issue"])
    for path in evidence_paths(manifest):
        if digest(path) != manifest["files"].get(path):
            raise ValueError(f"Evidence changed after packet creation: {path}; rebuild the packet")
    copied = read_json(manifest["requirements"])
    requirements(copied, manifest["issue"])
    if dispatcher.fingerprint(copied) != manifest["approval"]:
        raise ValueError("Copied requirements differ from the approved issue fingerprint; rebuild the packet")
    verification = read_json(manifest["verification"]["metadata"])
    if (verification.get("head") != manifest["head"] or manifest["verification"]["head"] != manifest["head"]
            or verification.get("command") != manifest["verification"]["command"]
            or verification.get("status") != manifest["verification"]["status"]):
        raise ValueError("Verification metadata must identify the reviewed head, command and result; rebuild the packet")
    if manifest.get("design"):
        required_file(manifest["design"]["path"])
        if not manifest["design"].get("approval", "").strip():
            raise ValueError("Design evidence needs its approval reference")
    reviewers = []
    for axis, report in manifest["reports"].items():
        required_file(report["path"])
        if (axis not in {"standards", "spec"} or not report["reviewer"].strip()
                or any(report[key] != manifest[key] for key in ("base", "head", "approval"))):
            raise ValueError("Review reports must identify this comparison, approval and reviewer")
        reviewers.append(report["reviewer"])
    if len(reviewers) != len(set(reviewers)):
        raise ValueError("Standards and Spec require different fresh reviewers")
    return manifest


def check(args):
    manifest = validate(args.manifest, args.base, args.head, args.approval)
    return {"status": manifest["status"], "manifest": str(args.manifest.resolve())}


def record(args):
    manifest = validate(args.manifest)
    reviewer = args.reviewer.strip()
    if not reviewer:
        raise ValueError("Review report needs a nonempty fresh reviewer ID")
    if any(value["reviewer"] == reviewer for axis, value in manifest["reports"].items() if axis != args.axis):
        raise ValueError("Standards and Spec require different fresh reviewers")
    report = required_file(args.report)
    if not report.read_text().strip():
        raise ValueError("Review report must contain actual evidence")
    manifest["reports"][args.axis] = {"reviewer": reviewer,
        "path": copy_file(report, args.manifest.resolve().parent, f"{args.axis}.md"),
        **{key: manifest[key] for key in ("base", "head", "approval")}}
    save_manifest(args.manifest.resolve(), manifest)
    return {"status": "recorded", "axis": args.axis, "manifest": str(args.manifest.resolve())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    create = actions.add_parser("build")
    create.add_argument("--issue", type=int, required=True)
    create.add_argument("--base", required=True)
    create.add_argument("--head", required=True)
    create.add_argument("--verification", type=Path, required=True)
    create.add_argument("--ticket", type=Path)
    create.add_argument("--approval")
    create.add_argument("--design", type=Path)
    create.add_argument("--design-approval")
    validate = actions.add_parser("check")
    validate.add_argument("--manifest", type=Path, required=True)
    validate.add_argument("--base")
    validate.add_argument("--head")
    validate.add_argument("--approval")
    report = actions.add_parser("record")
    report.add_argument("--manifest", type=Path, required=True)
    report.add_argument("--axis", choices=["standards", "spec"], required=True)
    report.add_argument("--reviewer", required=True)
    report.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps({"build": build, "check": check, "record": record}[args.action](args)))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(f"Review packet error: {error}", file=sys.stderr)
        sys.exit(1)
