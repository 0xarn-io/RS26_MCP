---
name: using-git-worktrees
description: Use within an activated workflow when authorized changes need an isolated workspace.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Use Isolation When Needed

First inspect the current workspace. Reuse an appropriate existing host sandbox or worktree;
do not create nested isolation automatically. A linked worktree alone does not prove nobody
else is using it. Confirm the branch, status, and ownership before relying on isolation.

Prefer the host's actual workspace tool when available and appropriate. Do not assume a tool
exists because another agent product has it. If it is unavailable, ordinary in-place editing
is valid when safe; otherwise use an explicitly authorized Git worktree.

## Manual Git option

Use `git worktree list --porcelain` and the current repository metadata to inspect state.
For a new worktree, follow the user's path and branch preference. Check that the destination
is absent/appropriate and that any project-local directory is ignored before creation.
Check the selected directory itself, not an unrelated alternative directory. Do not silently
modify `.gitignore`, commit changes, or choose a new branch without authority for those actions.

Record that this task created the workspace, its exact path/branch, and who owns cleanup.
A path under `.worktrees/` or `worktrees/` is not proof of ownership. Never remove a workspace
based on its name. Treat host-created and pre-existing workspaces as externally owned.

If creation is denied, do not bypass the restriction. Continue in place only when doing so is
within permissions and would not violate the reason isolation was needed; otherwise report
the blocker. There is no need to install dependencies automatically just because a worktree
is new. Inspect manifests and project instructions; run only authorized setup and tests.

Capture baseline test results when available. Distinguish existing failures from regressions.
Report limitations and continue independent safe work; ask only when a baseline failure
undermines the change's validity or safety. Follow the completion reference for handoff and
explicitly authorized cleanup.
