"""Deterministic regression tests. All Git mutations occur in temporary fixtures."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

AGENTS = Path(__file__).resolve().parents[1]
SDD = AGENTS / 'tools/sdd.py'
EXPORT = AGENTS / 'tools/export_context.py'
WRAPPERS = AGENTS / 'skills/super-code/references/subagent-driven-development/scripts'


class HelperTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='agent helpers ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'repository with spaces'
        self.root.mkdir()
        self.env = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull,
                        GIT_TERMINAL_PROMPT='0', PYTHONDONTWRITEBYTECODE='1')
        for name in list(self.env):
            if name in {'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR'}:
                del self.env[name]
        self.git('init', '-q', '--template=')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.git('config', 'user.name', 'Temporary Test Fixture')
        self.git('config', 'commit.gpgsign', 'false')
        self.git('config', 'core.autocrlf', 'false')
        self.plan = self.root / 'plan with spaces.md'
        self.plan.write_text('# Plan\n\n## Global Constraints\nKeep the public API.\n\n### Task 1: Update\nChange value and test it.\n', encoding='utf-8')
        (self.root / 'app.py').write_text('value = 1\n', encoding='utf-8')
        self.git('add', '--', self.plan.name, 'app.py')
        self.git('commit', '-qm', 'fixture baseline')
        self.base = self.git('rev-parse', 'HEAD').stdout.strip()

    def git(self, *args):
        result = subprocess.run(['git', *args], cwd=self.root, env=self.env,
                                capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def call(self, *args, code=0, cwd=None, executable=None):
        result = subprocess.run([sys.executable, str(executable or SDD), *map(str, args)],
                                cwd=cwd or self.root, env=self.env,
                                capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(result.returncode, code, f'args={args}\nstdout={result.stdout}\nstderr={result.stderr}')
        return result

    def workspace(self, plan=None):
        return Path(self.call('workspace', plan or self.plan).stdout.strip())

    def change(self, value=2, message='fixture implementation'):
        (self.root / 'app.py').write_text(f'value = {value}\n', encoding='utf-8')
        self.git('add', '--', 'app.py')
        self.git('commit', '-qm', message)
        return self.git('rev-parse', 'HEAD').stdout.strip()

    def record(self):
        self.change()
        directory = self.workspace()
        (directory / 'review.md').write_text('Fixture review: inspected value change and scope.\n', encoding='utf-8')
        (directory / 'tests.md').write_text('Fixture evidence only; no application correctness claim.\n', encoding='utf-8')
        self.call('complete', self.plan, 1, '--base', self.base, '--head', 'HEAD',
                  '--review', directory / 'review.md', '--tests', directory / 'tests.md')
        return directory

    def test_workspace_is_stable_and_ignored(self):
        before = self.git('status', '--porcelain').stdout
        first = self.workspace()
        self.assertEqual(first, self.workspace())
        self.assertTrue((first / 'manifest.json').is_file())
        self.assertEqual(before, self.git('status', '--porcelain').stdout)
        self.assertEqual(self.base, self.git('rev-parse', 'HEAD').stdout.strip())

    def test_second_plan_same_task_number_is_isolated(self):
        first = self.record()
        other = self.root / 'other plan.md'
        other.write_bytes(self.plan.read_bytes())
        second = self.workspace(other)
        self.assertNotEqual(first, second)
        result = json.loads(self.call('status', other, code=3).stdout)
        self.assertEqual(result['tasks'][0]['status'], 'pending')

    def test_edited_plan_gets_new_workspace(self):
        old = self.workspace()
        self.plan.write_text(self.plan.read_text() + '\nNew acceptance condition.\n')
        self.assertNotEqual(old, self.workspace())

    def test_legacy_ledger_is_not_imported(self):
        old = self.root / '.superpowers/sdd'
        old.mkdir(parents=True)
        (old / '.gitignore').write_text('*\n')
        (old / 'progress.md').write_text('Task 1: complete (review clean)\n')
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertEqual(result['tasks'][0]['status'], 'pending')
        self.assertEqual((old / 'progress.md').read_text(), 'Task 1: complete (review clean)\n')

    def test_status_without_workspace_is_read_only(self):
        self.call('status', self.plan, code=3)
        self.assertFalse((self.root / '.agents-state').exists())

    def test_default_brief_includes_global_constraints(self):
        out = Path(self.call('task-brief', self.plan, 1).stdout.strip())
        self.assertIn('Keep the public API.', out.read_text())
        self.assertIn('### Task 1: Update', out.read_text())
        self.assertEqual(out, Path(self.call('task-brief', self.plan, 1).stdout.strip()))

    def test_fenced_headings_are_not_tasks_and_footer_is_excluded(self):
        self.plan.write_text('''# Plan
Global context.
### Task 1: First
````markdown
### Task 80: Fenced
```
still inside
````
~~~python
### Task 81: Fenced
~~~~
    ### Task 82: Indented example
Real first task.
### Task 2: Second
Second task body.
## Appendix
Not part of task two.
''')
        one = Path(self.call('task-brief', self.plan, 1).stdout.strip()).read_text()
        two = Path(self.call('task-brief', self.plan, 2).stdout.strip()).read_text()
        self.assertIn('Real first task.', one)
        self.assertNotIn('Second task body.', one)
        self.assertNotIn('Not part of task two.', two)
        report = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertEqual([t['task'] for t in report['tasks']], [1, 2])

    def test_duplicate_task_numbers_fail_without_output(self):
        self.plan.write_text(self.plan.read_text() + '\n### Task 1: Duplicate\nOops\n')
        self.call('task-brief', self.plan, 1, code=2)
        self.assertFalse((self.root / '.agents-state').exists())

    def test_mixed_heading_levels_fail(self):
        self.plan.write_text(self.plan.read_text() + '\n## Task 2: Mixed\nOops\n')
        self.call('workspace', self.plan, code=2)

    def test_unclosed_fence_fails(self):
        self.plan.write_text(self.plan.read_text() + '\n```python\nvalue = 1\n')
        self.call('task-brief', self.plan, 1, code=2)

    def test_missing_task_creates_no_output(self):
        self.call('task-brief', self.plan, 999, code=2)
        self.assertFalse((self.root / '.agents-state').exists())

    def test_invalid_task_numbers_fail(self):
        for number in ('0', '-1', '01', 'abc', '1;echo bad'):
            with self.subTest(number=number):
                self.call('task-brief', self.plan, number, code=2)

    def test_explicit_output_is_idempotent_and_nonclobbering(self):
        out = self.root / 'brief export.md'
        self.call('task-brief', self.plan, 1, out)
        self.call('task-brief', self.plan, 1, out)
        out.write_text('existing user work\n')
        self.call('task-brief', self.plan, 1, out, code=2)
        self.assertEqual(out.read_text(), 'existing user work\n')

    def test_plan_and_git_metadata_cannot_be_output(self):
        original = self.plan.read_bytes()
        self.call('task-brief', self.plan, 1, self.plan, code=2)
        self.assertEqual(self.plan.read_bytes(), original)
        self.call('task-brief', self.plan, 1, self.root / '.git/HEAD', code=2)
        self.assertEqual(self.base, self.git('rev-parse', 'HEAD').stdout.strip())

    def test_outside_repository_paths_fail(self):
        outside = Path(self.temp.name) / 'outside.md'
        outside.write_bytes(self.plan.read_bytes())
        self.call('workspace', outside, code=2)
        self.call('task-brief', self.plan, 1, Path(self.temp.name) / 'output.md', code=2)

    def test_missing_output_parent_fails(self):
        self.call('task-brief', self.plan, 1, self.root / 'missing/output.md', code=2)
        self.assertFalse((self.root / 'missing').exists())

    def test_review_package_covers_multiple_commits(self):
        self.change(2, 'fixture change one')
        head = self.change(3, 'fixture change two')
        out = Path(self.call('review-package', self.plan, self.base, head).stdout.strip())
        text = out.read_text()
        self.assertIn('fixture change one', text)
        self.assertIn('fixture change two', text)
        self.assertIn('-value = 1', text)
        self.assertIn('+value = 3', text)
        self.assertIn('Committed changes only', text)

    def test_invalid_or_reversed_review_revisions_fail(self):
        head = self.change()
        self.call('review-package', self.plan, head, self.base, code=2)
        self.call('review-package', self.plan, 'missing-ref', 'HEAD', code=2)
        self.call('review-package', self.plan, 'HEAD;echo bad', 'HEAD', code=2)
        self.call('review-package', self.plan, '--', '-bad', 'HEAD', code=2)

    def test_receipt_validates_current_clean_head(self):
        directory = self.record()
        result = json.loads(self.call('status', self.plan).stdout)
        self.assertTrue(result['all_current'])
        self.assertEqual(result['tasks'][0]['status'], 'verified-current')
        self.assertTrue((directory / 'task-1-receipt.json').exists())

    def test_later_commit_requires_revalidation(self):
        self.record()
        self.change(3)
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertEqual(result['tasks'][0]['status'], 'needs-revalidation')
        self.assertTrue(any('HEAD changed' in reason for reason in result['tasks'][0]['reasons']))

    def test_dirty_tracked_file_invalidates_receipt(self):
        self.record()
        (self.root / 'app.py').write_text('value = 99\n')
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertTrue(result['dirty'])
        self.assertFalse(result['all_current'])

    def test_untracked_file_invalidates_receipt(self):
        self.record()
        (self.root / 'new_file.py').write_text('value = 99\n')
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertTrue(result['dirty'])

    def test_changed_evidence_invalidates_receipt(self):
        directory = self.record()
        (directory / 'tests.md').write_text('Changed evidence\n')
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertIn('tests evidence changed', result['tasks'][0]['reasons'])

    def test_missing_evidence_invalidates_receipt(self):
        directory = self.record()
        (directory / 'review.md').unlink()
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertFalse(result['all_current'])

    def test_corrupt_receipt_fails_closed(self):
        directory = self.record()
        (directory / 'task-1-receipt.json').write_text('not JSON\n')
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertEqual(result['tasks'][0]['status'], 'needs-revalidation')

    def test_mismatched_receipt_task_hash_fails_closed(self):
        directory = self.record()
        receipt = directory / 'task-1-receipt.json'
        data = json.loads(receipt.read_text())
        data['brief_sha256'] = 'wrong'
        receipt.write_text(json.dumps(data))
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertFalse(result['all_current'])

    def test_evidence_path_traversal_fails_closed(self):
        directory = self.record()
        receipt = directory / 'task-1-receipt.json'
        data = json.loads(receipt.read_text())
        data['review']['path'] = '../outside.md'
        receipt.write_text(json.dumps(data))
        result = json.loads(self.call('status', self.plan, code=3).stdout)
        self.assertFalse(result['all_current'])

    def test_complete_rejects_dirty_tree_without_changing_history(self):
        directory = self.record()
        before = self.git('rev-parse', 'HEAD').stdout
        (self.root / 'app.py').write_text('user work\n')
        self.call('complete', self.plan, 1, '--base', self.base, '--head', 'HEAD',
                  '--review', directory / 'review.md', '--tests', directory / 'tests.md', code=2)
        self.assertEqual(before, self.git('rev-parse', 'HEAD').stdout)
        self.assertEqual((self.root / 'app.py').read_text(), 'user work\n')

    def test_complete_rejects_old_head(self):
        directory = self.record()
        old = self.git('rev-parse', 'HEAD').stdout.strip()
        self.change(3)
        self.call('complete', self.plan, 1, '--base', self.base, '--head', old,
                  '--review', directory / 'review.md', '--tests', directory / 'tests.md', code=2)

    def test_complete_rejects_empty_evidence(self):
        directory = self.workspace()
        (directory / 'empty.md').write_text(' \n')
        self.call('complete', self.plan, 1, '--base', self.base, '--head', 'HEAD',
                  '--review', directory / 'empty.md', '--tests', directory / 'empty.md', code=2)

    def test_workspace_lock_refuses_second_writer_and_status(self):
        directory = self.workspace()
        lock = directory / '.lock'
        lock.write_text('pid=fixture\n')
        self.call('workspace', self.plan, code=2)
        self.call('status', self.plan, code=2)
        self.assertTrue(lock.exists())

    def test_bad_manifest_is_not_adopted(self):
        directory = self.workspace()
        (directory / 'manifest.json').write_text('{}\n')
        self.call('workspace', self.plan, code=2)
        self.call('status', self.plan, code=2)
        self.assertFalse((directory / '.lock').exists())

    def test_tracked_state_is_not_hidden(self):
        state = self.root / '.agents-state'
        state.mkdir()
        (state / 'important.txt').write_text('tracked file\n')
        self.git('add', '--', '.agents-state/important.txt')
        self.git('commit', '-qm', 'fixture tracked state')
        self.call('workspace', self.plan, code=2)
        self.assertFalse((state / '.gitignore').exists())

    @unittest.skipUnless(hasattr(os, 'symlink'), 'symlinks unavailable')
    def test_symlinked_state_is_rejected(self):
        outside = Path(self.temp.name) / 'outside state'
        outside.mkdir()
        try:
            (self.root / '.agents-state').symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest('symlink permission unavailable')
        self.call('workspace', self.plan, code=2)
        self.assertEqual(list(outside.iterdir()), [])

    def test_invocation_from_subdirectory(self):
        sub = self.root / 'nested'
        sub.mkdir()
        first = self.workspace()
        second = Path(self.call('workspace', self.plan, cwd=sub).stdout.strip())
        self.assertEqual(first, second)

    def test_missing_plan_argument_is_an_error(self):
        self.call('workspace', code=2)
        self.call('review-package', self.base, 'HEAD', code=2)

    @unittest.skipUnless(shutil.which('bash'), 'Bash unavailable; use Python entry point')
    def test_bash_wrappers_work_when_executable_bits_are_stripped(self):
        bundle = Path(self.temp.name) / 'copied bundle' / '.agents'
        shutil.copytree(AGENTS, bundle, ignore=shutil.ignore_patterns('__pycache__'))
        for path in bundle.rglob('*'):
            if path.is_file():
                path.chmod(0o644)
        scripts = bundle / WRAPPERS.relative_to(AGENTS)
        commands = [('sdd-workspace', [self.plan]), ('task-brief', [self.plan, 1]),
                    ('review-package', [self.plan, self.base, 'HEAD'])]
        for name, args in commands:
            with self.subTest(name=name):
                result = subprocess.run(['bash', str(scripts / name), *map(str, args)], cwd=self.root,
                                        env=dict(self.env, PYTHON=sys.executable), capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(Path(result.stdout.strip()).exists())


class ContextTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('context_export', EXPORT)
        cls.exporter = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.exporter)

    def test_prebuilt_context_matches_canonical_sources(self):
        self.assertEqual(self.exporter.render(), (AGENTS / 'AGENT_CONTEXT.md').read_text(encoding='utf-8'))

    def test_baseline_does_not_embed_skill_bodies(self):
        text = self.exporter.render()
        self.assertIn('Both', (AGENTS.parent / 'AGENTS.md').read_text())
        self.assertNotIn('## Included source: `.agents/skills/think-like-fable/SKILL.md`', text)
        self.assertNotIn('## Included source: `.agents/skills/super-code/SKILL.md`', text)

    def test_selected_skill_and_reference_are_embedded(self):
        text = self.exporter.render(['think-like-fable'], ['skills/think-like-fable/references/debugging.md'])
        self.assertIn('# Think Like Fable', text)
        self.assertIn('skills/think-like-fable/references/debugging.md', text)
        self.assertIn('does\nnot activate it', text)

    def test_reference_requires_its_skill(self):
        with self.assertRaises(ValueError):
            self.exporter.render([], ['skills/think-like-fable/references/debugging.md'])

    def test_unsafe_reference_is_rejected(self):
        for path in ('../outside.md', '/etc/passwd', 'AGENT_RULES.md', 'skills/think-like-fable/../../ISSUES.md'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.exporter.render(['think-like-fable'], [path])

    def test_exporter_refuses_to_clobber_nongenerated_output(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / 'important.md'
            out.write_text('user content\n')
            result = subprocess.run([sys.executable, str(EXPORT), '--output', str(out)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertEqual(out.read_text(), 'user content\n')

    def test_generated_export_can_be_refreshed(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / 'context.md'
            for args in ([], ['--skill', 'super-code']):
                result = subprocess.run([sys.executable, str(EXPORT), *args, '--output', str(out)], capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('# Super Code', out.read_text())


if __name__ == '__main__':
    unittest.main()
