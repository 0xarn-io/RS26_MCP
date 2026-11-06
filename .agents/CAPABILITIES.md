# Capability-based execution

Choose behavior from the host's exposed tools and permissions, not the model's name.
Do not require the user to fill out a capability questionnaire: inspect what is available
and mention limitations only when they matter. Never call a tool that is not exposed.

| Workflow concept | Preferred capability | Portable fallback |
|---|---|---|
| Read / search / edit | Host filesystem tools or approved shell | Work on supplied content; request only missing files that block correctness |
| Run commands / tests | Available authorized execution tool | Static review or concrete verification instructions, explicitly not executed |
| Load a skill | Host skill loader or direct file read | Read the supplied skill text; apply the same activation policy |
| Track tasks | Native task tracker | Markdown checklist in the session or an authorized local file |
| Delegate implementation / review | Actual subagent or collaborator tool | Execute sequentially and do a separate self-review pass; label it self-review |
| Select a model | Host-supported model selector and budget approval | Use the current model; never invent model identifiers or promise different quality |
| Isolate changes | Existing host sandbox/worktree | Work carefully in the current tree, or create an authorized Git worktree |
| Browse documentation | Authorized web/documentation access | Inspect local docs; identify any unresolved version-dependent assumption |
| Persist progress | Writable workspace and optional helper | Explicit handoff note with task identity, evidence, and revalidation requirements |
| Show a visual | Available approved image/browser capability | Text explanation; optional bundled server requires separate permission and dependencies |

Tool names in inherited examples (such as Read, Bash, Task, TodoWrite, or Skill) denote
capabilities, not commands to call literally. Prefer the actual host contract. A provider
API model is not, by itself, a filesystem agent or a subagent runtime.

## Environment requirements

The Markdown workflows have no runtime dependency. The optional deterministic task helpers
use Python 3.9+ and Git. Invoke `python .agents/tools/sdd.py --help` (or `python3` / `py -3`
for your installed interpreter). POSIX wrapper scripts additionally need Bash; on systems
without Bash use the Python entry point directly. The optional visual companion needs
Node.js and Bash; graph rendering also needs Graphviz. Do not install them automatically.

If Git is unavailable, use file diffs and a manual checklist. If commits are not authorized,
review the working-tree diff and record provisional evidence: do not manufacture a commit
for a helper. Commit-backed completion receipts are optional and more conservative than
human review. New sessions must revalidate provisional work against the actual files.

## Collaboration and state

Use one controller per plan per worktree. Parallel agents must not edit overlapping files
or share a progress ledger without coordination. Prefer separate worktrees for concurrent
controllers. Delegation does not expand scope or permissions; pass those boundaries to each
worker. Do not claim that a role-switch is an independent reviewer.

Read `.agents/tools/README.md` before using the helpers. They keep state per plan path and
content, not merely per worktree. They cannot prove correctness or authenticate a review;
manual examination and test evidence remain required.
