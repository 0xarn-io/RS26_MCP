---
name: writing-plans
description: Use within an activated workflow when a multi-step task needs an execution plan.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Write a Verifiable Plan

Plan from inspected files and agreed requirements. Match the level of detail to the risk;
small tasks can use an inline checklist. Use the repository's plan location when present;
otherwise use `docs/plans/YYYY-MM-DD-<feature>.md` if a file is warranted. Do not commit it
unless authorized. Do not write speculative complete implementations just to fill a template.

## Plan content

State the goal, relevant design, constraints, dependencies, and verification strategy.
Break work into tasks that can be implemented and checked independently. Each task names
files, interface contracts, acceptance criteria, and concrete verification commands or checks.
Include critical edge cases and any exact values the requirements mandate. Mark genuinely
unresolved questions as blockers, rather than inventing facts. Avoid vague steps such as
"add appropriate tests". Include code examples only where they clarify a real contract.

For compatibility with the optional task extractor, use unique positive task numbers and
ATX Markdown headings at a consistent level. The preamble is included in each task brief:

```markdown
# Feature implementation plan

**Goal:** The user-visible result.

## Global Constraints

Repository-wide requirements and task dependencies.

### Task 1: First independently verifiable result

**Files:** Exact relevant paths.
**Interfaces:** Inputs, outputs, and dependencies.
**Acceptance criteria:** Observable behavior, including important failure cases.
**Verification:** Exact runnable checks and expected results, or a labeled manual check.

- [ ] Reproduce or define the required behavior.
- [ ] Implement the smallest change.
- [ ] Run checks and review the actual diff.
```

## Self-review and execution

Map every requirement to a task. Check task ordering, interface consistency, scope, and
whether the checks would catch the feared failure. Do not include a mandatory commit step.

When execution is already requested, continue without an extra ceremony. Use sequential
execution by default; delegate only when the host supports it and the tasks benefit from it.
If only a plan was requested, deliver the plan and stop. Request a decision only when it is
actually required, not merely to choose between model-specific execution mechanisms.
