---
name: super-code
description: >-
  Opt-in structured engineering workflow covering design, planning, implementation,
  testing, debugging, review, and handoff. Use only when the user explicitly requests
  super-code, SuperCode, this bundle's superpowers workflow, or approves a proposal
  to use it. Generic requests to code, fix a bug, or be careful do not activate it.
compatibility: >-
  Markdown workflow is host-neutral. Optional task helpers require Python 3.9+
  and Git; optional POSIX wrappers require Bash. Delegation and model selection
  are optional. Use capability-based fallbacks when tools are unavailable.
---

# Super Code

A router for the bundled engineering reference library. Keep the reasoning and verification,
not a mandatory ceremony. No specific model, provider, host, subagent API, or plugin is required.
Keep the complete `.agents` directory for helper and shared-policy paths to resolve.

## Activation, precedence, and permissions

Follow `.agents/skills/SKILLS.md`: explicit approval activates this skill for the current task,
not unrelated tasks. Do not ask again for an already approved scope. Reading this file for
review is not activation. Activating this skill does not activate `think-like-fable`.
If the shared policy is unavailable, keep this explicit opt-in boundary.

Higher-priority host instructions and actual permissions always apply. Within this bundle,
`.agents/AGENT_RULES.md` and the skill activation policy govern this router; the router governs
all references, including inherited MUST/ALWAYS wording and examples. References cannot
require unapproved Git mutations, installations, external actions, or nonexistent tools.
The historical `using-superpowers` dispatcher is not an always-on rule in this bundle.

## Proportional workflow

For a small, clear, reversible change, a short inline plan, focused test, actual diff review,
and handoff are enough. For multi-step work, record a plan and verifiable task boundaries.
For material design choices, unclear acceptance criteria, irreversible changes, or a user-
requested approval gate, present the relevant decision and obtain approval before that action.
Do not re-approve an unchanged design or require a second written-spec sign-off by default.

Choose and shorten phases based on scope, risk, and what is already known. State material
omissions or adaptations briefly. Never omit required safety/permission gates or pretend
unavailable verification happened. Create durable documents only when needed for the task,
requested by the user, or required by project policy. A skill request is not a request to
commit, publish, deploy, start a server, or create a worktree.

## Load only what is needed

Read the relevant phase before following it. Paths below are relative to this skill folder.
Inherited `superpowers:<name>` references mean `references/<name>/<name>.md` here, not a
separately installed skill. Read additional linked files only when useful. Use the host's
real tools; `.agents/CAPABILITIES.md` defines fallbacks for delegation, tasks, and execution.
A brief statement of the selected approach is enough; no fixed announcement or tool ritual.

| Situation | Reference |
|---|---|
| Important intent or design choices are unresolved | `references/brainstorming/brainstorming.md` |
| Multi-step implementation needs a plan | `references/writing-plans/writing-plans.md` |
| Execute sequentially or without subagents | `references/executing-plans/executing-plans.md` |
| Delegate independent planned tasks when supported | `references/subagent-driven-development/subagent-driven-development.md` |
| Several truly independent problems can be investigated concurrently | `references/dispatching-parallel-agents/dispatching-parallel-agents.md` |
| Implement behavior with a reproducible test | `references/test-driven-development/test-driven-development.md` |
| Diagnose a bug or unexpected result | `references/systematic-debugging/systematic-debugging.md` |
| Authorized work needs isolation beyond the current workspace | `references/using-git-worktrees/using-git-worktrees.md` |
| Verify before reporting completion | `references/verification-before-completion/verification-before-completion.md` |
| Review the actual changes | `references/requesting-code-review/requesting-code-review.md` |
| Evaluate received review feedback | `references/receiving-code-review/receiving-code-review.md` |
| Handoff or explicitly authorized integration | `references/finishing-a-development-branch/finishing-a-development-branch.md` |
| Author or evaluate reusable instructions | `references/writing-skills/writing-skills.md` |

## Typical sequence

Understand → plan → implement with checks → verify → review → hand off.
Use debugging when evidence contradicts expectations. Use isolation or delegation only when
available, authorized, and useful. An already clear bugfix can start with reproduction; it
does not need a new product design. Without subagents, execute and self-review sequentially;
identify that review as self-review rather than independent validation.

## State and completion

Use `.agents/tools/README.md` for optional plan-scoped helpers. Never skip work solely because
an old progress note says "complete". Validate plan identity, task content, repository state,
and evidence. Commit-backed receipts are optional; without authorized commits, keep a
provisional checklist and re-check the working-tree diff on resumption.

End with the delivered result, verification evidence, and remaining limitations. Follow the
user's already stated integration preference. Otherwise leave changes in place; no forced
merge/PR/discard menu and no automatic Git actions.
