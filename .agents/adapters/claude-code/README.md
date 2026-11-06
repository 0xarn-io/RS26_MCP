# Optional Claude Code skill wrappers

File-based loading through root `CLAUDE.md` works without these wrappers. To expose native
slash commands, copy each `<name>/SKILL.md` from here to
`.claude/skills/<name>/SKILL.md` in the project. Merge rather than overwrite an existing skill.
These wrappers use a Claude Code-specific invocation flag; shared skills stay format-neutral.
Keep `.agents/` at the repository root. Wrappers read the canonical source instead of copying it.

Source: `.agents/SOURCES.md` [S6]. Test loading and invocation in the installed Claude Code host.
