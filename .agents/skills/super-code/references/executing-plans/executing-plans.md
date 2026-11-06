---
name: executing-plans
description: Use within an activated workflow to implement a plan sequentially with verification checkpoints.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Execute a Plan

Read the plan, inspect the repository, and check scope and dependencies before editing.
Use the same core process with or without subagents; do not claim a particular host or model
is required for quality. A missing delegation tool is not a blocker to sequential execution.

Resolve factual gaps by inspection. Ask once for conflicting requirements or missing authority
that affect the result. Otherwise execute the requested plan without repeated continuation
questions. Record a short checklist; a native task-tracking tool is optional.

For each task: capture the starting state, implement the scoped change, run relevant checks,
review the actual diff against its requirements, and record evidence and remaining concerns.
Use TDD where it can meaningfully reproduce behavior; for documentation/configuration or
unavailable hardware, use appropriate explicit checks instead of fictitious runtime tests.
Do not commit or create a worktree solely because the plan contains an inherited example.

For longer tasks, use the plan-scoped progress procedure in
`../subagent-driven-development/subagent-driven-development.md` and `.agents/tools/README.md`.
Without the helpers or authorized commits, keep a provisional checklist with file/diff identity;
on resume, inspect the actual files and revalidate rather than trusting a completion label.

Resolve regressions and important review findings before considering affected tasks complete.
Report unrelated baseline failures rather than hiding them. Pause only the blocked/risky portion
when work can safely continue elsewhere. Finish with verification, a separate self-review or
actual independent review, and `../finishing-a-development-branch/finishing-a-development-branch.md`.
