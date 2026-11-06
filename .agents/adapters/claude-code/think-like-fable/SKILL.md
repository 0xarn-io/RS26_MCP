---
name: think-like-fable
description: Explicitly invoke this project's shared think-like-fable workflow.
disable-model-invocation: true
---

Read `.agents/AGENT_RULES.md`, `.agents/skills/SKILLS.md`, and
`.agents/skills/think-like-fable/SKILL.md` from the repository root, then follow the shared entry point.
An explicit user invocation grants activation for the current task; do not ask again for that
same scope. If loaded for review or discovered incidentally, do not activate the skill.
If a source is unavailable, identify the missing context instead of inventing its contents.
Use only actual host tools and permissions; no additional skill or external action is implied.
