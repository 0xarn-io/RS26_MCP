# Optional deterministic helpers

The Markdown workflows need no runtime. These helpers require Python 3.9+ and Git on PATH.
They use only the Python standard library and invoke Git without a shell. Run them inside
the target Git repository. Plan/evidence paths are resolved from the current directory;
the source plan must be a UTF-8 Markdown file inside that repository. They do not pull,
commit, push, reset, merge, clean, run tests, or contact a remote service.

## Task workspace and extraction

```sh
python .agents/tools/sdd.py workspace docs/plans/example.md
python .agents/tools/sdd.py task-brief docs/plans/example.md 1
python .agents/tools/sdd.py task-brief docs/plans/example.md 1 task-1-export.md
```

Default paths are under `.agents-state/sdd/<plan-slug>-<identity>/` in the current worktree.
The identity hashes the resolved worktree root, repository-relative plan path, and exact
plan bytes. Different plans, copied plans at different paths, changed plans, or a relocated
checkout do not silently reuse a ledger. A matching manifest is required before state reuse.
The top-level scratch `.gitignore` contains `*`; tracked files there cause write operations
to fail rather than being hidden. No repository-level ignore file is edited.

Plans use unique positive numbers in ATX headings such as `### Task 1: Name`, all at the
same level. The extractor ignores headings inside backtick/tilde fences, includes the full
preamble/global constraints, and stops at the next heading at the task's level or higher.
Indented-code pseudo-headings are not parsed as tasks. Unclosed fences, duplicate task IDs,
missing tasks, and mixed task-heading levels fail with no brief output. Keep global
requirements in the preamble, not a footer or between tasks.

Keep the source plan unchanged during a run; update a separate checklist or receipts.
Even ticking a plan checkbox changes its bytes and starts a new identity. Plan edits are
allowed, but old completion evidence must then be reviewed rather than auto-imported.

Output paths are printed to stdout. An explicit OUTFILE must be a new repository-local file
outside managed state, with an existing parent directory. It cannot replace the plan or
Git metadata. Existing files with identical content are reused; different contents are never
clobbered. Omit OUTFILE to use generated state paths. Symlinked state, plans, and evidence are
rejected rather than followed. This is accidental-clobber protection, not an adversarial sandbox.

## Review packages

```sh
python .agents/tools/sdd.py review-package docs/plans/example.md BASE HEAD
```

Replace BASE and HEAD with the recorded commits; BASE must be an ancestor of HEAD.
The output contains the complete commit list, file summary, and net committed diff with
extended context. It handles multi-commit tasks; do not substitute `HEAD~1` for the real base.
Uncommitted/staged/new files are not in that package and must be reviewed separately.
External diff/text-conversion drivers are disabled for these Git diff calls.

## Completion receipts and resume checks

Only use commit-backed completion when commits were already authorized. Otherwise keep
provisional notes and review the working-tree diff; do not create a commit to satisfy a helper.
Keep review/test evidence in the printed scratch directory so it does not dirty the worktree.
Evidence must identify what was reviewed/tested, code identity, commands/results, limitations,
and whether the review was independent or self-review. A nonempty file is not itself proof.

```sh
python .agents/tools/sdd.py complete docs/plans/example.md 1 --base BASE --head HEAD --review PATH_TO_REVIEW_NOTE --tests PATH_TO_TEST_EVIDENCE
python .agents/tools/sdd.py status docs/plans/example.md
```

`complete` requires current HEAD, valid commit ancestry, a clean tree (including untracked
files), and nonempty UTF-8 evidence. It atomically records task/plan identity and evidence
hashes. It does not execute or authenticate tests/reviews. The report may explicitly record
unavailable checks: read those limitations before accepting completion. Never substitute a
receipt for checking whether the user's actual acceptance criteria were satisfied.

`status` is read-only. It returns per-task `pending`, `needs-revalidation`, or `verified-current`.
A changed HEAD, dirty tree, invalid commits, changed/missing evidence, or a bad receipt prevents
`verified-current`. That label means identity checks passed, not that the code is correct.
Only consider skipping such a task after reading its evidence. Every later commit makes earlier
receipts conservative/stale; revalidate the earlier requirement against the new code before
renewing its evidence and receipt. Do not simply retag old reports with a new HEAD.

Exit codes: `0` successful helper operation (`status`: every task is verified-current),
`2` invalid input/environment/state, `3` (`status` only) one or more pending/stale tasks.
Never infer completion from the presence of a progress file or a task number alone.

## Concurrency, recovery, and limits

One controller per plan per worktree. Writers use an exclusive workspace lock and fail rather
than overwrite another controller's state. JSON receipts are replaced atomically. A crash may
leave a lock; inspect it and confirm the owning process is no longer running before an
explicit manual removal. The lock is not a lock on the repository or a whole agent session.
Use separate worktrees for concurrent controllers and re-check the snapshot before acting.

No state/evidence checksum here establishes authenticity: anyone able to rewrite both can
forge a matching record. Scratch files are not durable backups and can disappear during
cleanup. Reconstruct from actual changes and fresh checks. Old `.superpowers/sdd` ledgers are
not imported, trusted, deleted, or modified automatically.

## POSIX wrappers and migration

The three original wrapper filenames are retained with executable permissions. Running them
through `bash` also works when a ZIP extractor strips executable bits. They resolve the Python
helper directly rather than executing another non-executable helper. Native Windows can invoke
the Python entry point without Bash. Set `PYTHON` to a Python executable path when needed
(no embedded arguments; use the Python entry point directly for `py -3`).

| Wrapper | Revised interface |
|---|---|
| `sdd-workspace` | `sdd-workspace PLAN_FILE` (plan is now required) |
| `task-brief` | `task-brief PLAN_FILE TASK_NUMBER [OUTFILE]` |
| `review-package` | `review-package PLAN_FILE BASE HEAD [OUTFILE]` (plan is now required) |

All wrappers live under `skills/super-code/references/subagent-driven-development/scripts/`.
No old no-plan fallback remains, because it would recreate shared-ledger ambiguity.

## Exporting chat context

`export_context.py` combines canonical Markdown without needing Git. The default is baseline,
activation policy, and capability fallbacks. `--skill NAME` adds a skill; repeat `--reference`
with paths relative to `.agents` for selected reference files. `--project` includes architecture,
issues, and roadmap; review those files for sensitive content before sharing. The exporter
writes stdout unless `--output` is supplied and refuses to overwrite nongenerated files.
