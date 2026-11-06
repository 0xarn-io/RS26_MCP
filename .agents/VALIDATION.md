# Validation record — 2026-09-13

## Executed checks

53 distinct deterministic unit/contract tests passed, with no failures or skips, in four
isolated test groups (14 + 13 + 13 + 13). The nine static contract tests were re-run after
the final lower-level wording corrections and passed again. Two earlier monolithic runner
attempts hit execution time limits; they were not counted as completed suites. The full
53-test set was subsequently completed in the four groups recorded below.

The tests exercise default helper paths, executable-bit loss, spaces in paths, plan isolation,
changed plans, global constraints, fenced headings, malformed/duplicate tasks, multi-commit
review packages, dirty worktrees, stale HEAD, changed/missing/corrupt evidence, receipt identity,
invalid revisions, non-clobbering output, symlink/traversal rejection, locks, and context export.
They create Git commits only inside temporary fixtures, never in the user's repository.

Additional checks passed:

- Six Bash syntax checks and three JavaScript/CommonJS syntax checks (nine total).
- All four new Python files parsed using Python 3.9 grammar.
- Actual YAML parsing and field validation for the two shared skills and two optional Claude
  wrappers; both optional OpenAI metadata files parsed with implicit invocation disabled.
- Concrete local Markdown links in active documents resolved. Historical illustrative links
  were excluded, not asserted to be valid current resources.
- The generated AGENT_CONTEXT.md exactly matched the canonical baseline sources.

Execution environment: Linux x86_64; Python 3.13.5; Git 2.47.3; Bash 5.2.37;
Node.js 22.16.0. Python 3.9 grammar validation is not a runtime test on Python 3.9.
Optional format checks used the environment's YAML parser; the shipped helpers and unit tests
require only Python's standard library, Git, and Bash for the wrapper-specific test.

## Not executed or established

No live Claude Code, Grok, GPT/Codex, or other provider-model sessions were run. Host loading
was checked against the official documentation in SOURCES.md, not proven by a live client.
The manual cases in tests/BEHAVIORAL_EVALS.md remain behavioral tests to run in deployment.

Native Windows and macOS execution, browser/visual-companion runtime, Graphviz output,
project application tests, and physical machinery behavior were not exercised. Optional
Node/Bash reference utilities were syntax-checked, not comprehensively audited for security.
No target-project build commands or machine-specific safe-state requirements were supplied.

Markdown guidance does not enforce tool permissions, guarantee identical model behavior,
or prove correct code. Completion receipts validate identity/evidence consistency at a
snapshot; they do not authenticate reviews/tests or lock the repository against later changes.

## Reproduce

From the installed repository root:

```sh
python -m unittest discover -s .agents/tests -p 'test_*.py' -v
```

Use the appropriate Python 3.9+ invocation for the operating system. Bash wrapper tests skip
when Bash is unavailable; symlink tests may skip on platforms lacking symlink permission.
See tests/TEST_RESULTS.txt for the actual completed runs used for this distribution.
