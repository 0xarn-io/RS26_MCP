"""Static checks for the portable contract; not live agent-behavior evaluations."""
import ast
import os
from pathlib import Path
import re
import unittest

A = Path(__file__).resolve().parents[1]


class ContractTests(unittest.TestCase):
    def test_root_entry_point_and_claude_imports_share_sources(self):
        entry = (A.parent / 'AGENTS.md').read_text()
        adapter = (A.parent / 'CLAUDE.md').read_text()
        for path in ('.agents/AGENT_RULES.md', '.agents/skills/SKILLS.md'):
            self.assertIn(path, entry)
            self.assertIn('@' + path, adapter)
        self.assertIn('@AGENTS.md', adapter)

    def test_shared_skill_frontmatter_is_portable(self):
        for directory in (A / 'skills').iterdir():
            if not directory.is_dir():
                continue
            with self.subTest(skill=directory.name):
                text = (directory / 'SKILL.md').read_text()
                front = text.split('---', 2)[1]
                name = re.search(r'^name: (.+)$', front, re.M)[1]
                self.assertEqual(name, directory.name)
                self.assertRegex(name, r'^[a-z0-9]+(?:-[a-z0-9]+)*$')
                self.assertLessEqual(len(name), 64)
                description = re.search(r'^description: >-\n((?:  .*\n)+)', front, re.M)
                self.assertIsNotNone(description)
                self.assertLessEqual(len(' '.join(line.strip() for line in description[1].splitlines())), 1024)
                self.assertIn('only when the user explicitly', text.lower())
                self.assertNotIn('disable-model-invocation:', front)
                self.assertNotIn('allowed-tools:', front)
                self.assertLess(len(text.splitlines()), 500)

    def test_optional_host_invocation_controls_are_present(self):
        for name in ('think-like-fable', 'super-code'):
            self.assertIn('allow_implicit_invocation: false', (A / 'skills' / name / 'agents/openai.yaml').read_text())
            self.assertIn('disable-model-invocation: true', (A / 'adapters/claude-code' / name / 'SKILL.md').read_text())

    def test_no_original_activation_or_pull_conflict(self):
        baseline = (A / 'AGENT_RULES.md').read_text()
        fable = (A / 'skills/think-like-fable/SKILL.md').read_text()
        router = (A / 'skills/super-code/SKILL.md').read_text()
        self.assertNotIn('Always pull and check first', baseline)
        self.assertNotIn("Trigger even when the user doesn't", fable)
        self.assertNotIn("Only skip a skill's workflow when the user has told you to", router)
        self.assertNotIn('Fault states de-energize outputs.', (A / 'skills/think-like-fable/references/twincat-rules.md').read_text())

    def test_active_router_reference_paths_exist(self):
        for name in ('super-code', 'think-like-fable'):
            directory = A / 'skills' / name
            text = (directory / 'SKILL.md').read_text()
            for relative in re.findall(r'`(references/[^`]+\.md)`', text):
                if '<' not in relative:
                    with self.subTest(path=relative):
                        self.assertTrue((directory / relative).is_file())

    def test_review_package_calls_include_plan_identity(self):
        text = (A / 'skills/super-code/references/subagent-driven-development/subagent-driven-development.md').read_text()
        self.assertNotIn('scripts/review-package BASE HEAD', text)
        self.assertIn('scripts/review-package PLAN_FILE BASE HEAD', text)
        self.assertNotIn('as complete are DONE', text)

    def test_all_reference_docs_have_scope_or_historical_notice(self):
        for path in (A / 'skills/super-code/references').rglob('*.md'):
            with self.subTest(path=str(path)):
                text = path.read_text()
                self.assertTrue('> Scope: use only within' in text or '> Historical/reference material' in text)

    def test_python_helpers_parse_with_python39_grammar(self):
        for path in (A / 'tools').glob('*.py'):
            with self.subTest(path=path.name):
                ast.parse(path.read_text(), filename=str(path), feature_version=(3, 9))

    def test_executable_entry_points_have_lf(self):
        names = {'sdd-workspace', 'task-brief', 'review-package'}
        for path in A.rglob('*'):
            if path.is_file() and (path.suffix == '.sh' or path.name in names or path.parent == A / 'tools' and path.suffix == '.py'):
                with self.subTest(path=str(path)):
                    self.assertNotIn(b'\r\n', path.read_bytes())
                    if os.name == 'posix':
                        self.assertTrue(path.stat().st_mode & 0o111, f'missing executable bit: {path}')


if __name__ == '__main__':
    unittest.main()
