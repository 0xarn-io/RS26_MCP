---
name: finishing-a-development-branch
description: Use within an activated workflow to deliver verified changes or perform explicitly authorized integration.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Verify and Hand Off

Re-read the original request. Inspect the final diff and repository state. Report what changed,
what actually ran, the results, and remaining limitations. Do not claim test success based on
an old report for different code. Address regressions; identify pre-existing failures separately.

The default finish is to leave the changes in place and provide the handoff. Do not force a
merge/PR/discard menu. Follow an already stated integration preference without asking again.
If no integration was requested, finishing the coding task does not authorize a commit, pull,
merge, rebase, push, PR, deployment, branch deletion, or worktree removal.

## Authorized integration

Before an authorized Git operation, inspect the precise source and destination, branch state,
existing changes, and expected effect. Never assume the base is `main` or `master`. Do not run
`git pull` as a generic precondition. If synchronization is needed, explain the proposed action
and obtain the required authority. A PR request may authorize its necessary push, but it does
not authorize unrelated cleanup, force-pushing, or broad publication of private files.

Recheck the integration result with relevant tests. If verification cannot run, report that
fact and do not represent the result as verified. A user may request a draft PR with known
failures; label them rather than claiming readiness.

## Cleanup

Only remove a task-created workspace when its provenance is recorded, its current contents
have been inspected, and cleanup is authorized. Pre-existing/host-owned workspaces are not
ours to remove. A directory name is not evidence of ownership. Verify work is preserved before
removal; never use forced deletion to clear an unexpected failure. Destructive discard needs
clear user authorization for the exact affected work. Do not prune unrelated registrations.
