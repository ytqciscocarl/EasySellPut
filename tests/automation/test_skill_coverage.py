import unittest
from skill_coverage import START, END, validate_skill


class SkillCoverageTests(unittest.TestCase):
    def setUp(self):
        self.operations = {'get_quotes': {'writes': False}, 'save_strategy': {'writes': True}}
        self.content = (START + '\n| Tool | Access | When to use / constraints |\n'
                        '| --- | --- | --- |\n'
                        '| `get_quotes` | read | Read quotes and retain source timestamps. |\n'
                        '| `save_strategy` | write | Save only an explicitly requested strategy. |\n' + END)

    def test_complete_guide_passes(self):
        self.assertEqual(validate_skill(self.operations, self.content), 2)

    def test_new_tool_requires_guidance(self):
        with self.assertRaisesRegex(AssertionError, 'missing='):
            validate_skill({**self.operations, 'get_news': {'writes': False}}, self.content)

    def test_removed_or_renamed_tool_fails(self):
        with self.assertRaisesRegex(AssertionError, 'stale='):
            validate_skill({'get_quotes': {'writes': False}}, self.content)

    def test_permission_drift_fails(self):
        with self.assertRaisesRegex(AssertionError, 'access mismatch'):
            validate_skill(self.operations, self.content.replace('| write |', '| read |'))

    def test_duplicate_entry_fails(self):
        with self.assertRaisesRegex(AssertionError, 'Duplicate'):
            validate_skill(self.operations, self.content.replace(END, '| `get_quotes` | read | Read quotes and retain source timestamps. |\n' + END))

    def test_placeholder_guidance_fails(self):
        with self.assertRaisesRegex(AssertionError, 'useful guidance'):
            validate_skill(self.operations, self.content.replace('Read quotes and retain source timestamps.', 'TODO'))

    def test_stale_workflow_reference_fails(self):
        with self.assertRaisesRegex(AssertionError, 'workflow tool'):
            validate_skill(self.operations, self.content + '\nUse `removed_tool`.')

    def test_missing_section_fails(self):
        with self.assertRaisesRegex(AssertionError, 'exactly one'):
            validate_skill(self.operations, '')
