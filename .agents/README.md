# Portable agent instruction bundle

Shared rules and optional engineering workflows for any assistant whose host can read
Markdown or receive it as supplied context. The model/provider and the agent application
are different layers: file discovery, tools, approvals, and persistence belong to the host.
No filename guarantees automatic loading or identical compliance in every application.

## Install without overwriting project instructions

Extract the archive into a staging directory first. Review/merge `.agents/` into the target
repository, preserving any project-specific changes. Put the supplied `AGENTS.md` at the
repository root, not only inside `.agents/`. If a root `AGENTS.md` already exists, merge its
loader instructions rather than replacing the project's rules. Do the same for `CLAUDE.md`.
The supplied ZIP contains relative paths and no symlinks or package-install hooks.

The baseline lives in `.agents/AGENT_RULES.md`; activation lives in `.agents/skills/SKILLS.md`.
`AGENTS.md` and `CLAUDE.md` are small loaders, not independent copies of the rulebook.
Keep the whole `.agents` directory for shared helper paths to resolve. Fill
`ARCHITECTURE.md` with real project commands and constraints; none have been invented here.

## Loading by host

| Host / environment | Loading path |
|---|---|
| Codex or another host that reads `AGENTS.md` | Use repository-root `AGENTS.md`, which directs the agent to the shared rules. Codex's project discovery and `.agents/skills` discovery are separate. [S1, S2] |
| Claude Code | Use repository-root `CLAUDE.md`. Its `@` imports load the same entry point, baseline, and activation policy. Claude Code documents this bridge for `AGENTS.md`. [S3] |
| Grok-, GPT-, Claude-, or other model-powered IDE/custom agent | Configure the actual host to load `AGENTS.md`, or inject the generated context below into its project-instruction mechanism. Do not assume a `GROK.md` filename or special tool exists. |
| Chat-only interface or host without repository access | Supply `AGENT_CONTEXT.md` as text or an attachment through whatever context mechanism the host supports. Add relevant project files and explicitly requested skills. A local path alone is not its contents. |
| Custom API application | Read the context in the application and pass it through the API's supported instruction/context fields, respecting the provider's role rules. Implement and authorize filesystem/execution tools separately. The xAI text API, for example, documents instruction messages; that does not imply local file discovery. [S4] |

Sources [S1]-[S7] and verification date are in `SOURCES.md`. Loading instructions are based
on those documents; live Claude/Grok/GPT sessions were not run as part of this revision.
Confirm actual loading and behavior in each deployed host using `tests/BEHAVIORAL_EVALS.md`.
Markdown is guidance, not a sandbox or a hard authorization control.

## Portable use

For a filesystem-capable host, an explicit initial prompt can be:

> Read `AGENTS.md`, `.agents/AGENT_RULES.md`, and `.agents/skills/SKILLS.md` as this project's
> instructions, within your host's permissions. Work on the task using only tools actually
> available. Keep both optional skills inactive unless I explicitly request one.

For a chat-only host, supply the contents of `AGENT_CONTEXT.md` instead of only those paths.
The prebuilt context includes the baseline, activation policy, and capability fallbacks;
it does not include the entire skill library or project source code. No scripts are needed
to use that supplied context. Regenerate after changing canonical rules:

```sh
python .agents/tools/export_context.py --output .agents/AGENT_CONTEXT.md
```

An export for a specific task may add a skill and selected references:

```sh
python .agents/tools/export_context.py --skill think-like-fable --reference skills/think-like-fable/references/debugging.md --output fable-debugging-context.md
```

Use `python3` or `py -3` if that is how Python 3.9+ is installed. The exporter only reads
local files and writes the requested context file. It makes no API calls and activates no
skill. Review exported project content before sharing it with an external service.

## Skill activation and optional native adapters

Say `Use think-like-fable for this task` or `Use super-code for this task` to opt in.
A generic request to fix a bug, discovery, or reading the files does not opt in. Approval
lasts for the current task and direct continuations; do not ask again for the same scope.
Reviewing or editing a skill is not executing its workflow. Activating one does not activate
the other. Careful baseline engineering requires no extra permission prompt.

The shared skills use the open Agent Skills frontmatter structure. [S5] Native discovery is
host-dependent. Optional Codex `agents/openai.yaml` files disable implicit invocation on
hosts supporting that setting; other hosts use the same explicit prose policy. [S2]
For optional Claude Code slash-command registration, copy the small wrappers in
`adapters/claude-code/` into the corresponding `.claude/skills/<name>/SKILL.md` locations.
They use Claude Code's `disable-model-invocation` flag and delegate to the shared source. [S6]
Those wrappers are not required for normal file-based use. Do not copy vendor-specific
fields into the shared format or assume another host honors them.

## Optional helpers and verification

`tools/sdd.py` supplies plan-scoped task briefs, review packages, and conservative completion
receipts. Markdown/manual fallbacks work without Python, Git, Bash, or subagents. See
`tools/README.md` for commands, changed interfaces, and limitations. The visual companion
and graph utilities remain optional and were not browser/hardware tested.

Run the bundled deterministic tests from the repository root:

```sh
python -m unittest discover -s .agents/tests -p 'test_*.py' -v
```

Tests use temporary repositories and never commit to the target project. They create test
commits only inside those disposable fixtures. See `VALIDATION.md` for actual results and
unverified environments. Behavioral evaluation cases are separate and are not a claim of
live cross-model testing. See `CHANGELOG.md` and `PROVENANCE.md` for the revision history.
