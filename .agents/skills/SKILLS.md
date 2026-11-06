# Optional skills and activation

The baseline rules apply without activating either skill. Ordinary careful reasoning,
testing, and code review do not require a skill or a permission question.

| Skill | Purpose | Entry point |
|---|---|---|
| `think-like-fable` (Fablethinking) | Deeper reasoning, uncertainty reduction, and verification | `.agents/skills/think-like-fable/SKILL.md` |
| `super-code` (SuperCode / this bundle's superpowers workflow) | Structured design, plan, implementation, and review | `.agents/skills/super-code/SKILL.md` |

## One activation contract, on every host

Activate a skill only when the user explicitly requests it by name, invokes it through a
host skill control, or accepts a clear proposal to use it. That grants activation for the
current task and its direct continuations, not unrelated future tasks. Do not ask again for
the same approved scope. Revocation or a changed task ends that approval.

Discovery, a broad description match, an attached file, a saved ledger, "be careful", or
"fix this bug" is not activation. Reading a skill to inspect/edit it is not activating it.
Do not ask permission for every routine task: use the baseline unless an optional workflow
would materially help. Activating one skill does not silently activate the other. If both
are requested, Fable supplies reasoning habits and Super Code supplies phase structure.

After activation, read the entry point and only relevant references. No slash command,
special tool name, particular model, or plugin is required: file reading or supplied text
is enough for the prose workflow. Native loading controls are optional host adapters, not
a portable security boundary. The host's actual restrictions always remain in force.
