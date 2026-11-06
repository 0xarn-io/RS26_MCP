#!/usr/bin/env python3
"""Plan-scoped, optional workflow helpers. Python 3.9+, Git; standard library only.

Never mutates Git history. State is local recovery evidence, not authentication.
Run from within the target repository; see README.md for the command contract.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from typing import Iterator

SCHEMA = 1
STATE_DIR = ".agents-state"


class WorkflowError(Exception):
    """Invalid input, unsafe destination, or unverifiable state."""


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def git(root: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", GIT_PAGER="cat")
    result = subprocess.run(
        ["git", "--no-pager", "-c", "color.ui=false", *args], cwd=root,
        env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        encoding="utf-8", errors="replace", check=False,
    )
    if check and result.returncode:
        raise WorkflowError(result.stderr.strip() or f"git {' '.join(args)} failed")
    return result


def repository() -> Path:
    return Path(git(Path.cwd(), "rev-parse", "--show-toplevel").stdout.strip()).resolve()


def within(root: Path, path: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError as exc:
        raise WorkflowError(f"path must stay inside the current repository: {path}") from exc


def reject_symlinks(root: Path, path: Path) -> None:
    relative = within(root, path)
    current = root
    for part in Path(relative).parts:
        current = current / part
        if current.is_symlink():
            raise WorkflowError(f"symlink is not allowed for workflow state/evidence: {current}")


def local_file(root: Path, value: str) -> Path:
    raw = Path(os.path.abspath(value))
    reject_symlinks(root, raw)
    path = raw.resolve()
    relative = within(root, path)
    if relative == ".git" or relative.startswith(".git/"):
        raise WorkflowError("Git metadata is not an artifact or evidence destination")
    return path


def commit(root: Path, ref: str) -> str:
    if not ref or ref.startswith("-") or "\x00" in ref:
        raise WorkflowError(f"invalid revision: {ref!r}")
    result = git(root, "rev-parse", "--verify", "--quiet", ref + "^{commit}", check=False)
    if result.returncode:
        raise WorkflowError(f"not a commit: {ref!r}")
    return result.stdout.strip()


def ancestor(root: Path, base: str, head: str) -> bool:
    result = git(root, "merge-base", "--is-ancestor", base, head, check=False)
    if result.returncode not in (0, 1):
        raise WorkflowError(result.stderr.strip() or "cannot verify commit ancestry")
    return result.returncode == 0


def headings(text: str) -> list:
    """ATX headings outside backtick/tilde fences; preserve the original task text."""
    found = []
    fence = None
    for number, line in enumerate(text.splitlines(keepends=True)):
        marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line.rstrip("\r\n"))
        if fence:
            if marker and marker[1][0] == fence[0] and len(marker[1]) >= fence[1] and not marker[2].strip():
                fence = None
            continue
        if marker:
            # Backtick fence info strings cannot themselves contain backticks.
            if marker[1][0] != "`" or "`" not in marker[2]:
                fence = (marker[1][0], len(marker[1]))
                continue
        match = re.match(r"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+[ \t]*)?$", line.rstrip("\r\n"))
        if match:
            task = re.match(r"Task[ \t]+([0-9]+)(?=[: \t]|$)", match[2])
            if task and (int(task[1]) < 1 or task[1].startswith("0")):
                raise WorkflowError("task numbers must be positive integers without leading zeros")
            found.append((number, len(match[1]), int(task[1]) if task else None))
    if fence:
        raise WorkflowError("plan has an unclosed fenced code block")
    return found


class Plan:
    def __init__(self, root: Path, value: str):
        self.root = root
        self.path = local_file(root, value)
        if not self.path.is_file():
            raise WorkflowError(f"plan file not found: {self.path}")
        self.relative = within(root, self.path)
        if self.relative.split("/")[0] == STATE_DIR:
            raise WorkflowError("keep the source plan outside generated workflow state")
        self.data = self.path.read_bytes()
        self.text = self.data.decode("utf-8")
        self.lines = self.text.splitlines(keepends=True)
        self.headings = headings(self.text)
        tasks = [item for item in self.headings if item[2] is not None]
        self.tasks = [item[2] for item in tasks]
        if not tasks:
            raise WorkflowError("plan needs an ATX heading such as '### Task 1: Name'")
        if len(set(self.tasks)) != len(self.tasks):
            raise WorkflowError("duplicate task numbers are not allowed")
        if len({item[1] for item in tasks}) != 1:
            raise WorkflowError("all Task headings must use the same heading level")
        self.preamble = "".join(self.lines[:tasks[0][0]]).strip()
        self.identity = {
            "schema": SCHEMA, "worktree": str(root),
            "plan_path": self.relative, "plan_sha256": sha(self.data),
        }
        self.key = sha(json.dumps(self.identity, sort_keys=True).encode("utf-8"))[:24]
        slug = re.sub(r"[^a-zA-Z0-9_-]+", "-", self.path.stem).strip("-")[:32] or "plan"
        self.workspace = root / STATE_DIR / "sdd" / (slug + "-" + self.key)

    def brief(self, task: int) -> str:
        matches = [item for item in self.headings if item[2] == task]
        if not matches:
            raise WorkflowError(f"task {task} not found in {self.relative}")
        start, level, _ = matches[0]
        end = next((item[0] for item in self.headings if item[0] > start and item[1] <= level), len(self.lines))
        body = "".join(self.lines[start:end]).strip()
        return (f"# Task {task} brief\n\nSource plan: `{self.relative}`\n"
                f"Plan SHA-256: `{self.identity['plan_sha256']}`\n\n"
                f"## Plan context and global constraints\n\n{self.preamble}\n\n"
                f"## Selected task\n\n{body}\n")

    def check_unchanged(self) -> None:
        if self.path.read_bytes() != self.data:
            raise WorkflowError("plan changed during the operation; rerun with the current plan")


def atomic_json(path: Path, value: dict) -> None:
    """Replace controller-owned state atomically. Caller holds the workspace lock."""
    if path.is_symlink():
        raise WorkflowError(f"refusing to replace symlink: {path}")
    fd, temporary = tempfile.mkstemp(prefix=".sdd-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def immutable_output(root: Path, path: Path, text: str) -> None:
    """Exclusive creation, or idempotent reuse; never clobber a different file."""
    reject_symlinks(root, path)
    if not path.parent.is_dir():
        raise WorkflowError(f"output parent does not exist: {path.parent}")
    data = text.encode("utf-8")
    try:
        with path.open("xb") as stream:
            stream.write(data)
    except FileExistsError:
        if not path.is_file() or path.read_bytes() != data:
            raise WorkflowError(f"output already exists with different contents: {path}")


def check_workspace(plan: Plan) -> bool:
    reject_symlinks(plan.root, plan.workspace)
    if not plan.workspace.exists():
        return False
    manifest = plan.workspace / "manifest.json"
    reject_symlinks(plan.root, manifest)
    if not manifest.is_file() or json.loads(manifest.read_text(encoding="utf-8")) != plan.identity:
        raise WorkflowError("workspace identity is missing or inconsistent; do not reuse this state")
    return True


@contextmanager
def locked_workspace(plan: Plan) -> Iterator[Path]:
    root, directory = plan.root, plan.workspace
    reject_symlinks(root, directory)
    if git(root, "ls-files", "--", STATE_DIR).stdout.strip():
        raise WorkflowError(f"{STATE_DIR} contains tracked files; refuse to hide or modify them")
    directory.mkdir(parents=True, exist_ok=True)
    immutable_output(root, root / STATE_DIR / ".gitignore", "*\n")
    lock = directory / ".lock"
    try:
        fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError as exc:
        raise WorkflowError("workspace is locked; use one controller, do not delete a live lock") from exc
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(f"pid={os.getpid()}\n")
        manifest = directory / "manifest.json"
        reject_symlinks(root, manifest)
        if manifest.exists():
            check_workspace(plan)
        else:
            # A previous incomplete initialization may leave only the ignore/lock files.
            unexpected = [p.name for p in directory.iterdir() if p.name != ".lock"]
            if unexpected:
                raise WorkflowError("unidentified existing workspace contents; refusing to adopt them")
            atomic_json(manifest, plan.identity)
        plan.check_unchanged()
        yield directory
    finally:
        lock.unlink()


def evidence(root: Path, value: str) -> dict:
    path = local_file(root, value)
    if not path.is_file():
        raise WorkflowError(f"evidence file not found: {path}")
    data = path.read_bytes()
    if not data.decode("utf-8").strip():
        raise WorkflowError(f"evidence must be nonempty UTF-8 text: {path}")
    return {"path": within(root, path), "sha256": sha(data)}


def clean(root: Path) -> bool:
    return not git(root, "status", "--porcelain", "--untracked-files=all").stdout.strip()


def receipt_path(plan: Plan, task: int) -> Path:
    return plan.workspace / f"task-{task}-receipt.json"


def complete(plan: Plan, args: argparse.Namespace) -> dict:
    brief = plan.brief(args.task)
    base, head = commit(plan.root, args.base), commit(plan.root, args.head)
    if head != commit(plan.root, "HEAD"):
        raise WorkflowError("completion HEAD must be the current checked-out commit")
    if not ancestor(plan.root, base, head):
        raise WorkflowError("BASE must be an ancestor of HEAD")
    with locked_workspace(plan):
        if not clean(plan.root):
            raise WorkflowError("worktree is dirty; keep provisional notes, do not commit without authorization")
        review = evidence(plan.root, args.review)
        tests = evidence(plan.root, args.tests)
        path = receipt_path(plan, args.task)
        if path in (plan.root / review["path"], plan.root / tests["path"]):
            raise WorkflowError("a receipt cannot serve as its own evidence")
        record = {
            "schema": SCHEMA, "workspace_id": plan.key, "task": args.task,
            "brief_sha256": sha(brief.encode("utf-8")), "base": base, "head": head,
            "review": review, "tests": tests,
            "recorded_at": datetime.now(timezone.utc).isoformat(),
        }
        plan.check_unchanged()
        if not clean(plan.root) or commit(plan.root, "HEAD") != head:
            raise WorkflowError("repository changed during completion; revalidate first")
        atomic_json(path, record)
    return {"receipt": str(path), "status": "recorded", "note": "Evidence hashes are not proof of correctness."}


def status(plan: Plan) -> tuple:
    exists = check_workspace(plan)
    if exists and (plan.workspace / ".lock").exists():
        raise WorkflowError("workspace is locked; status requires a consistent controller snapshot")
    current = commit(plan.root, "HEAD")
    dirty = not clean(plan.root)
    tasks = []
    for task in plan.tasks:
        path = receipt_path(plan, task)
        reject_symlinks(plan.root, path)
        if not exists or not path.exists():
            tasks.append({"task": task, "status": "pending", "reasons": ["no completion receipt"]})
            continue
        reasons = []
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
            if record.get("schema") != SCHEMA or record.get("workspace_id") != plan.key or record.get("task") != task:
                reasons.append("receipt identity mismatch")
            if record.get("brief_sha256") != sha(plan.brief(task).encode("utf-8")):
                reasons.append("task or global constraints changed")
            base, head = commit(plan.root, record["base"]), commit(plan.root, record["head"])
            if not ancestor(plan.root, base, head) or not ancestor(plan.root, head, current):
                reasons.append("recorded commits are not on the current history")
            if head != current:
                reasons.append("HEAD changed since review; revalidate against current code")
            for kind in ("review", "tests"):
                saved = record[kind]
                actual = evidence(plan.root, str(plan.root / saved["path"]))
                if actual != saved:
                    reasons.append(f"{kind} evidence changed")
            if dirty:
                reasons.append("worktree is dirty; committed receipt cannot validate these files")
        except (KeyError, TypeError, AttributeError, ValueError, OSError, WorkflowError) as exc:
            reasons.append(f"invalid or unavailable receipt/evidence: {exc}")
        tasks.append({"task": task, "status": "needs-revalidation" if reasons else "verified-current", "reasons": reasons})
    plan.check_unchanged()
    # This is a snapshot, not a lock on the repository. Detect obvious concurrent changes.
    if commit(plan.root, "HEAD") != current or (not clean(plan.root)) != dirty:
        raise WorkflowError("repository changed during status; rerun before using the result")
    all_current = all(task["status"] == "verified-current" for task in tasks)
    return {"workspace": str(plan.workspace), "workspace_exists": exists, "head": current,
            "dirty": dirty, "all_current": all_current, "tasks": tasks}, (0 if all_current else 3)


def positive(value: str) -> int:
    if not re.fullmatch(r"[1-9][0-9]*", value):
        raise argparse.ArgumentTypeError("task number must be a positive integer without leading zeros")
    return int(value)


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__)
    commands = p.add_subparsers(dest="command", required=True)
    for name in ("workspace", "task-brief", "review-package", "complete", "status"):
        cmd = commands.add_parser(name)
        cmd.add_argument("plan", help="UTF-8 plan file inside the current Git repository")
        if name in ("task-brief", "complete"):
            cmd.add_argument("task", type=positive)
        if name == "review-package":
            cmd.add_argument("base")
            cmd.add_argument("head")
        if name in ("task-brief", "review-package"):
            cmd.add_argument("outfile", nargs="?", help="new output file; never overwrites different contents")
        if name == "complete":
            for flag in ("base", "head", "review", "tests"):
                cmd.add_argument("--" + flag, required=True)
    return p


def main(argv=None) -> int:
    args = parser().parse_args(argv)
    try:
        plan = Plan(repository(), args.plan)
        if args.command == "workspace":
            with locked_workspace(plan) as directory:
                print(directory)
        elif args.command in ("task-brief", "review-package"):
            if args.command == "task-brief":
                text, name = plan.brief(args.task), f"task-{args.task}-brief.md"
            else:
                base, head = commit(plan.root, args.base), commit(plan.root, args.head)
                if not ancestor(plan.root, base, head):
                    raise WorkflowError("BASE must be an ancestor of HEAD")
                log = git(plan.root, "log", "--no-show-signature", "--format=%h %s", base + ".." + head).stdout
                stat = git(plan.root, "diff", "--no-ext-diff", "--no-textconv", "--stat", base, head, "--").stdout
                diff = git(plan.root, "diff", "--no-ext-diff", "--no-textconv", "--binary", "-U10", base, head, "--").stdout
                text = (f"# Review package: {base}..{head}\n\n"
                        "Committed changes only; inspect uncommitted and untracked files separately.\n\n"
                        f"## Commits\n\n{log}\n## Files changed\n\n{stat}\n## Diff\n\n{diff}")
                name = f"review-{base}..{head}.diff"
            plan.check_unchanged()
            if args.outfile:
                out = local_file(plan.root, args.outfile)
                if out == plan.path or STATE_DIR in Path(within(plan.root, out)).parts:
                    raise WorkflowError("explicit output must not replace the plan or target managed state; omit OUTFILE for default state")
                immutable_output(plan.root, out, text)
            else:
                with locked_workspace(plan) as directory:
                    out = directory / name
                    immutable_output(plan.root, out, text)
            print(out)
        elif args.command == "complete":
            print(json.dumps(complete(plan, args), indent=2))
        else:
            report, code = status(plan)
            print(json.dumps(report, indent=2))
            return code
        return 0
    except (WorkflowError, OSError, ValueError) as exc:
        print(f"sdd: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
