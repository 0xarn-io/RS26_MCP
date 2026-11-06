---
name: brainstorming
description: Use within an activated workflow when material requirements or design choices are unresolved.
---

> Scope: use only within an activated `super-code` workflow. The shared baseline,
> activation policy, and `super-code/SKILL.md` router govern this reference. Examples
> do not authorize commits or external actions. Map tools to actual host capabilities;
> use sequential execution and labeled self-review when delegation is unavailable.

# Clarify Intent and Design

Start from the request and the actual repository. Identify the outcome, acceptance criteria,
constraints, and the uncertainty that could invalidate the approach. Resolve factual questions
by inspection before asking the user. Do not invent a required approval step for a tiny edit.

## Scale the design to the decision

For clear, small, reversible work, state a short approach and proceed within the task's scope.
For substantial work, compare genuinely plausible alternatives and recommend one with the
important tradeoffs. Ask only about material preferences, missing authority, or choices that
would change the destination. Do not manufacture three alternatives for an obvious fix.

Present an integrated design at the level needed to review it. Obtain approval where the
user requested a design gate or a consequential unresolved choice requires one. Existing
explicit approval remains valid unless the design or risk changes materially. Do not require
approval for each section followed by a second approval of identical prose in a file.

## Documents and handoff

If a durable design is useful, use the repository's established documentation location;
otherwise use `docs/designs/YYYY-MM-DD-<topic>.md`. Include scope, interfaces, failure modes,
verification, and open decisions. Self-review for contradictions and missing requirements.
Writing a design does not authorize committing it. Keep a small design inline when sufficient.

Continue with `../writing-plans/writing-plans.md` when a multi-step plan is warranted, or with
the relevant implementation/testing phase for a small task. Do not ask whether to continue
when implementation was already requested and no material decision is outstanding.

## Optional visuals

Use a visual only when it helps the decision and the host supports it. The bundled
`visual-companion.md` and its Node/Bash scripts are optional, environment-specific utilities.
Starting a server, opening a browser, or exposing a port requires authorization and an
appropriate local environment. Text is a valid fallback. Approval of this skill alone is
not approval to launch a server; inspect its configuration before use.
