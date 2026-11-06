---
name: requesting-code-review
description: Use within an activated workflow to check scope, correctness, tests, and maintainability.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Review the Actual Change

Review the actual changes against the request and acceptance criteria. Use an independent
reviewer when the host exposes one and the task benefits from it. Otherwise perform a separate
self-review pass and say it was self-review; do not invent a subagent or model selector.

Record the real baseline before implementation. For committed work, review the full task
range, including all fix commits; do not guess `HEAD~1` or derive scope from a commit-message
search. For uncommitted work, inspect both staged and unstaged changes, new files, and the
starting diff so existing user edits remain distinguishable. A committed diff package does
not include uncommitted files and cannot substitute for their review.

Give the reviewer the requirements, constraints, scoped diff, and test evidence, without
priming conclusions. Use [code-reviewer.md](code-reviewer.md) as a rubric, adapting its fields
to the evidence available. A literal `general-purpose` tool type is not required. Tell any
worker the permission boundaries, including whether commits or external actions are authorized.

Resolve critical and important correctness/scope findings, verify fixes, and re-review the
changed range. Record minor issues for a final triage instead of silently dropping them.
Check claims against actual code. Rerun tests when evidence is missing, stale, inconsistent,
or insufficient; do not trust a report simply because another agent wrote it.
