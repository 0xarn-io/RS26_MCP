# Revision: portable agent bundle — 2026-09-13

## Shared contract

Added root `AGENTS.md`, Claude Code imports, one agent-neutral baseline, explicit opt-in
activation, and capability-based fallbacks. Added a generated context export for hosts without
filesystem discovery. No model name or special agent tool is required for the prose workflows.
Optional native metadata/adapters do not replace the portable policy or host security controls.

Removed conflicting autoactivation, universal design approvals, automatic pull/commit/cleanup,
and mandatory model/subagent assumptions from the active entry points. Retained Fable's core
reasoning guidance and the reference library. Labeled inherited/vendor examples as subordinate
or historical; restored a clear authority boundary. Ordinary careful work remains the default.

## Helpers

Replaced the three fragile Bash-only task helpers with thin executable wrappers over a
standard-library Python implementation. The plan argument is now required for workspace and
review-package. State moved from worktree-wide `.superpowers/sdd` to identity-scoped
`.agents-state/sdd/...`. Different plans or edits never automatically share completion state.

Added validated task extraction, global-constraint inclusion, fence handling, duplicate-task
rejection, no-clobber output, conservative completion receipts, evidence hashing, status checks,
Git revision validation, and a writer lock. These are recovery checks, not proof of correctness,
cryptographic authentication, a repository lock, or permission to create commits.

Old state is left untouched and not migrated automatically. See `tools/README.md` for the
intentional CLI changes, snapshot limits, source-plan immutability, and manual fallbacks.

## Other corrections

Replaced blanket output de-energization with approved machine-specific safe-state requirements.
Removed the misleading self-checksum audit claim. Corrected the roadmap entry-point name.
Preserved the original `/input` AGV documentation hint as explicitly project-specific context;
left unknown project build commands and architecture facts unfilled rather than guessing.
Added input provenance, documentation sources, deterministic tests, behavioral evaluation cases,
and a validation record. Browser companion, hardware behavior, and live cross-model adherence
remain separate verification tasks, not claimed test results.
